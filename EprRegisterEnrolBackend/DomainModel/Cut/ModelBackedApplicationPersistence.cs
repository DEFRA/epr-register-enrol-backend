using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Cut;

/// <summary>
/// RA-706 decision 6 - with LegacyWrites off this replaces the legacy persistence; reads project from the model;
/// the version is the model's, the concurrency token after the cut.
/// </summary>
public sealed class ModelBackedApplicationPersistence(
    IMongoDbClientFactory clientFactory,
    ApplicationGroupStores stores,
    ApplicationReadModel readModel,
    CutStagingPersistence staging,
    Func<CutFlush> flush,
    ILogger<ModelBackedApplicationPersistence> logger
) : IAccreditationApplicationPersistence
{
    private async Task<T> RunInOwnTransactionAsync<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        return await InsertRaceRetry.RunInTransactionAsync(
            clientFactory,
            async (session, token) =>
            {
                var cut = flush();
                cut.Begin(session);
                var result = await write(session, token);
                await cut.FlushAsync(session, token);
                return result;
            },
            attempt => logger.LogWarning("Model-backed write lost an insert race and will retry: attempt {Attempt}", attempt),
            cancellationToken);
    }

    private async Task<List<AccreditationApplicationModel>> ProjectCurrentAsync(
        FilterDefinition<ApplicationEntity> envelope,
        IClientSessionHandle? session,
        CancellationToken ct)
    {
        using var own = session is null ? await clientFactory.GetClient().StartSessionAsync(cancellationToken: ct) : null;
        var s = session ?? own!;

        var candidates = await stores.Applications.Versions.FindAllCurrentAsync(
            envelope & Builders<ApplicationEntity>.Filter.Eq("applicationType", nameof(ApplicationType.Accreditation)),
            s,
            ct
        );

        var projected = new List<AccreditationApplicationModel>();
        foreach (var candidate in candidates)
        {
            var p = session != null
                ? await ReadAsync(candidate.EntityId, s, ct)
                : await ProjectAsync(candidate.EntityId, s, ct);
            if (p != null) projected.Add(p);
        }

        return projected;
    }

    private async Task<AccreditationApplicationModel?> ReadAsync(ObjectId id, IClientSessionHandle session, CancellationToken ct)
    {
        return await staging.GetByObjectIdAsync(id, session, ct) ?? await ProjectAsync(id, session, ct);
    }

    private async Task EnsureStagedAsync(ObjectId id, IClientSessionHandle session, CancellationToken ct)
    {
        if (await staging.GetByObjectIdAsync(id, session, ct) is null)
        {
            var projected = await ProjectAsync(id, session, ct);
            if (projected != null)
            {
                await staging.StageAsync(projected, session, ct);
            }
        }
        else
        {
            staging.Track(session, id);
        }
    }

    private static IClientSessionHandle RequireTransaction(IClientSessionHandle? session)
    {
        if (session != null && session.IsInTransaction) return session;
        throw new NotSupportedException("RA-706: a write with a session must run in its transaction");
    }

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private async Task<List<AccreditationApplicationModel>> HoldingAccreditationReferenceAsync(string reference, CancellationToken ct)
    {
        var accreditations = await stores.Accreditations.Versions.FindAllCurrentAsync(Builders<AccreditationEntity>.Filter.Eq(a => a.AccreditationNumber, reference), null, ct);
        var accreditationIds = accreditations.Select(a => a.EntityId).ToList();
        var envelope = Builders<ApplicationEntity>.Filter.Eq("details.reservedAccreditationNumber", reference) | Builders<ApplicationEntity>.Filter.In("details.accreditationId", accreditationIds);
        return (await ProjectCurrentAsync(envelope, null, ct)).Where(a => a.AccreditationReference == reference).ToList();
    }

    private async Task<AccreditationApplicationModel?> ProjectAsync(
        ObjectId id,
        IClientSessionHandle session,
        CancellationToken ct
    )
    {
        var sources = await readModel.LoadAsync(id, session, ct);
        if (sources is null)
            return null;

        var legacy = ApplicationProjection.ToLegacy(sources);

        legacy.Version = sources.Application.Version;
        legacy.CreatedAt = sources.Application.CreatedAt ?? sources.Application.ValidFrom;
        legacy.UpdatedAt = sources.Application.ValidFrom;

        legacy.RegistrationEntityId = sources.Application.RegistrationId;
        legacy.AccreditationEntityId = (
            (AccreditationDetails)sources.Application.Details
        ).AccreditationId;

        return legacy;
    }

    public Task<AccreditationApplicationModel?> GetByObjectIdAsync(
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    ) => ReadAsync(applicationId, session, cancellationToken);

    public async Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId,
        IClientSessionHandle? session = null)
    {
        if (!ObjectId.TryParse(applicationId, out var id)) return null;

        using var own = session is null ? await clientFactory.GetClient().StartSessionAsync() : null;
        var s = session ?? own!;
        var projected = await ReadAsync(id, s, default);

        return projected?.OrganisationId == organisationId ? projected : null;
    }

    public async Task<AccreditationApplicationModel?> CreateAsync(AccreditationApplicationModel application)
    {
        try
        {
            return await RunInOwnTransactionAsync(async (own, token) =>
            {
                await staging.StageAsync(application, own, token);
                return application;
            }, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to insert accreditation application for org={OrganisationId}", application.OrganisationId);
            return null;
        }
    }

    public async Task UpsertAsync(AccreditationApplicationModel application)
    {
        await RunInOwnTransactionAsync(async (own, token) => { await staging.DeleteStagedAsync([application.Id!.Value], own, token); await staging.StageAsync(application, own, token); return true; }, CancellationToken.None);
    }

    public Task<long> DeleteManyAsync(IEnumerable<ObjectId> ids) =>
        throw new NotSupportedException("RA-706: not yet served from the model");

    public async Task<IEnumerable<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId)
    {
        var envelope = Builders<ApplicationEntity>.Filter.Eq(a => a.ReExOrganisationId, organisationId);
        var results = await ProjectCurrentAsync(envelope, null, CancellationToken.None);
        return results.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).ToList();
    }

    public async Task<AccreditationApplicationModel?> GetLiveByRegistrationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year,
        IClientSessionHandle? session = null)
    {
        var envelope = Builders<ApplicationEntity>.Filter.Eq(a => a.ReExOrganisationId, organisationId)
            & Builders<ApplicationEntity>.Filter.Eq(a => a.ReExRegistrationId, registrationId);

        var results = await ProjectCurrentAsync(envelope, session, CancellationToken.None);
        return results
            .Where(a => a.MaterialType == materialType && a.Year == year && a.ApplicationStatus != ApplicationStatus.Withdrawn)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .FirstOrDefault();
    }

    public async Task<AccreditationApplicationModel?> GetLatestPreviousAccreditationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        GlassRecyclingProcess? glassRecyclingProcess,
        int beforeYear)
    {
        var envelope = Builders<ApplicationEntity>.Filter.Eq(a => a.ReExOrganisationId, organisationId)
            & Builders<ApplicationEntity>.Filter.Eq(a => a.ReExRegistrationId, registrationId);

        var results = await ProjectCurrentAsync(envelope, null, CancellationToken.None);
        return results
            .Where(a => a.MaterialType == materialType
                && a.GlassRecyclingProcess == glassRecyclingProcess
                && a.Year < beforeYear
                && a.ApplicationStatus == ApplicationStatus.Approved
                && !string.IsNullOrEmpty(a.AccreditationReference))
            .OrderByDescending(a => a.Year)
            .ThenByDescending(a => a.DateLastEdited)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<AccreditationReferenceHolder>> GetOtherAccreditationReferenceHoldersAsync(
        string accreditationReference,
        ObjectId applicationId)
    {
        var holders = await HoldingAccreditationReferenceAsync(accreditationReference, CancellationToken.None);
        return holders
            .Where(a => a.Id != applicationId)
            .Select(a => new AccreditationReferenceHolder(a.Id!.Value, a.ApplicationStatus))
            .ToList();
    }

    public async Task<AccreditationApplicationModel?> GetGrantedAccreditationAsync(
        int orgId,
        string registrationNumber,
        int year,
        int firstYear
    )
    {
        var organisation = await stores.Organisations.FindByOrganisationNumberAsync(orgId, null, CancellationToken.None);
        if (organisation is null) return null;

        var envelope = Builders<ApplicationEntity>.Filter.Eq(a => a.Status, ApplicationStatus.Approved)
            & Builders<ApplicationEntity>.Filter.Eq(a => a.ReExOrganisationId, organisation.ReExOrganisationId);

        var results = await ProjectCurrentAsync(envelope, null, CancellationToken.None);

        return results
            .Where(a => a.OrgId == orgId
                && a.RegistrationReference == registrationNumber
                && a.Year == year
                && a.Year >= firstYear
                && a.ApplicationStatus == ApplicationStatus.Approved
                && !IsBlank(a.AccreditationReference))
            .OrderByDescending(a => a.DateLastEdited)
            .FirstOrDefault();
    }

    public async Task<(IReadOnlyList<AccreditationApplicationModel> Items, long Total)> SearchGrantedAccreditationsAsync(
        GrantedAccreditationFilter filter,
        int firstYear,
        int skip,
        int limit
    )
    {
        var envelope = Builders<ApplicationEntity>.Filter.Eq(a => a.Status, ApplicationStatus.Approved);
        if (filter.OrganisationNumber.HasValue)
        {
            var organisation = await stores.Organisations.FindByOrganisationNumberAsync(filter.OrganisationNumber.Value, null, CancellationToken.None);
            if (organisation is null) return ([], 0);
            envelope &= Builders<ApplicationEntity>.Filter.Eq(a => a.ReExOrganisationId, organisation.ReExOrganisationId);
        }

        var results = await ProjectCurrentAsync(envelope, null, CancellationToken.None);

        var granted = results.Where(a =>
            a.OrgId != null
            && a.Year >= firstYear
            && a.ApplicationStatus == ApplicationStatus.Approved
            && !IsBlank(a.AccreditationReference)
            && !IsBlank(a.RegistrationReference)
            && (!filter.Year.HasValue || a.Year == filter.Year)
            && (!filter.OrganisationNumber.HasValue || a.OrgId == filter.OrganisationNumber)
            && (filter.RegistrationNumber is null || a.RegistrationReference == filter.RegistrationNumber)
        ).ToList();

        var total = (long)granted.Count;
        var items = granted
            .OrderBy(a => a.AccreditationReference, StringComparer.Ordinal)
            .ThenBy(a => a.Id)
            .Skip(skip)
            .Take(limit)
            .ToList();

        return (items, total);
    }

    public async Task<UnservableApprovedCounts> CountUnservableApprovedAsync(
        int firstYear,
        CancellationToken cancellationToken = default
    )
    {
        var envelope = Builders<ApplicationEntity>.Filter.Eq(a => a.Status, ApplicationStatus.Approved);
        var results = await ProjectCurrentAsync(envelope, null, cancellationToken);

        var approved = results.Where(a => a.ApplicationStatus == ApplicationStatus.Approved && a.Year >= firstYear).ToList();

        return new UnservableApprovedCounts(
            approved.Count(a => a.OrgId == null),
            approved.Count(a => IsBlank(a.RegistrationReference)),
            approved.Count(a => IsBlank(a.AccreditationReference))
        );
    }

    public async Task<IReadOnlyList<string>> GetOrsIdsByRegistrationAsync(
        string registrationId,
        IClientSessionHandle? session = null)
    {
        var envelope = Builders<ApplicationEntity>.Filter.Eq(a => a.ReExRegistrationId, registrationId);
        var results = await ProjectCurrentAsync(envelope, session, CancellationToken.None);
        return results
            .SelectMany(a => a.OverseasSites?.Sites ?? [])
            .Select(s => s.OrsId)
            .OfType<string>()
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetInterimSiteNumbersByRegistrationAsync(
        string registrationId,
        IClientSessionHandle? session = null)
    {
        var envelope = Builders<ApplicationEntity>.Filter.Eq(a => a.ReExRegistrationId, registrationId);
        var results = await ProjectCurrentAsync(envelope, session, CancellationToken.None);
        return results
            .SelectMany(a => a.OverseasSites?.Sites ?? [])
            .SelectMany(InterimSiteSync.All)
            .Select(i => i.SiteNumber)
            .OfType<string>()
            .ToList();
    }

    public async Task<AccreditationApplicationModel?> UpdateIfInterimSiteNumberAbsentAsync(AccreditationApplicationModel application, string siteNumber, IClientSessionHandle? session = null)
    {
        if (session is null)
        {
            return await RunInOwnTransactionAsync((own, token) => UpdateIfInterimSiteNumberAbsentAsync(application, siteNumber, own), CancellationToken.None);
        }
        var s = RequireTransaction(session);
        await EnsureStagedAsync(application.Id!.Value, s, CancellationToken.None);
        return await staging.UpdateIfInterimSiteNumberAbsentAsync(application, siteNumber, s);
    }

    public async Task<AccreditationApplicationModel?> GetNewestForOrganisationAsync(
        string organisationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var envelope = Builders<ApplicationEntity>.Filter.Eq(a => a.ReExOrganisationId, organisationId);
        var results = await ProjectCurrentAsync(envelope, session, cancellationToken);
        return results.OrderByDescending(a => a.UpdatedAt).ThenByDescending(a => a.Id).FirstOrDefault();
    }

    public async Task<IReadOnlyList<AccreditationApplicationModel>> GetRegistrationGroupAsync(
        string organisationId,
        string registrationId,
        int limit,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var envelope = Builders<ApplicationEntity>.Filter.Eq(a => a.ReExOrganisationId, organisationId)
            & Builders<ApplicationEntity>.Filter.Eq(a => a.ReExRegistrationId, registrationId);

        var results = await ProjectCurrentAsync(envelope, session, cancellationToken);

        if (session != null)
        {
            foreach (var id in staging.StagedIds(session))
            {
                if (results.Any(a => a.Id == id)) continue;
                var staged = await staging.GetByObjectIdAsync(id, session, cancellationToken);
                if (staged is not null && staged.OrganisationId == organisationId && staged.RegistrationId == registrationId)
                {
                    results.Add(staged);
                }
            }
        }

        return results.Take(limit).ToList();
    }

    public async Task<AccreditationApplicationModel?> GetByCaseManagementWorkItemIdAsync(
        Guid workItemId,
        IClientSessionHandle? session = null)
    {
        var envelope = Builders<ApplicationEntity>.Filter.Eq(a => a.CaseManagementWorkItemId, workItemId);
        var results = await ProjectCurrentAsync(envelope, session, CancellationToken.None);
        return results.FirstOrDefault(a => a.CaseManagementWorkItemId == workItemId);
    }

    public async Task<IReadOnlyList<AccreditationApplicationModel>> GetByAccreditationReferenceAsync(
        string accreditationReference,
        CancellationToken cancellationToken = default)
    {
        return await HoldingAccreditationReferenceAsync(accreditationReference, cancellationToken);
    }

    public async Task<IReadOnlyList<AccreditationApplicationModel>> GetByOrgIdAndYearAsync(
        int orgId,
        int year,
        CancellationToken cancellationToken = default)
    {
        var organisation = await stores.Organisations.FindByOrganisationNumberAsync(orgId, null, cancellationToken);
        if (organisation is null) return [];

        var envelope = Builders<ApplicationEntity>.Filter.Eq(a => a.ReExOrganisationId, organisation.ReExOrganisationId);
        var results = await ProjectCurrentAsync(envelope, null, cancellationToken);
        return results
            .Where(a => a.OrgId == orgId && a.Year == year)
            .OrderBy(a => a.Id)
            .ToList();
    }

    public async Task<AccreditationApplicationModel?> UpdateAsync(AccreditationApplicationModel application, IClientSessionHandle? session = null)
    {
        if (session is null)
        {
            return await RunInOwnTransactionAsync((own, token) => UpdateAsync(application, own), CancellationToken.None);
        }
        var s = RequireTransaction(session);
        await EnsureStagedAsync(application.Id!.Value, s, CancellationToken.None);
        return await staging.UpdateAsync(application, s);
    }

    public async Task<AccreditationApplicationModel?> UpdateIfOrsIdAbsentAsync(AccreditationApplicationModel application, string orsId, IClientSessionHandle? session = null)
    {
        if (session is null)
        {
            return await RunInOwnTransactionAsync((own, token) => UpdateIfOrsIdAbsentAsync(application, orsId, own), CancellationToken.None);
        }
        var s = RequireTransaction(session);
        await EnsureStagedAsync(application.Id!.Value, s, CancellationToken.None);
        return await staging.UpdateIfOrsIdAbsentAsync(application, orsId, s);
    }

    public async Task<AccreditationApplicationModel?> UpdateFieldsAsync(ObjectId id, FilterDefinition<AccreditationApplicationModel>? guardFilter, UpdateDefinition<AccreditationApplicationModel> update, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        if (session is null)
        {
            return await RunInOwnTransactionAsync((own, token) => UpdateFieldsAsync(id, guardFilter, update, own, token), cancellationToken);
        }
        var s = RequireTransaction(session);
        await EnsureStagedAsync(id, s, cancellationToken);
        return await staging.UpdateFieldsAsync(id, guardFilter, update, s, cancellationToken);
    }

    public async Task StampDomainModelForeignKeysAsync(ObjectId applicationId, ObjectId registrationEntityId,
        IReadOnlyDictionary<string, ObjectId> overseasSiteEntityIds, ObjectId? accreditationEntityId,
        IClientSessionHandle session, CancellationToken cancellationToken = default)
    {
        if (await staging.GetByObjectIdAsync(applicationId, session, cancellationToken) is null) return;
        await staging.StampDomainModelForeignKeysAsync(applicationId, registrationEntityId, overseasSiteEntityIds, accreditationEntityId, session, cancellationToken);
    }
}
