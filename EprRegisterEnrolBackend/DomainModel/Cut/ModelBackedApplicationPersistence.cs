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
    ApplicationReadModel readModel
) : IAccreditationApplicationPersistence
{
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
            var p = await ProjectAsync(candidate.EntityId, s, ct);
            if (p != null) projected.Add(p);
        }

        return projected;
    }

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
    ) => ProjectAsync(applicationId, session, cancellationToken);

    public async Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId,
        IClientSessionHandle? session = null)
    {
        if (!ObjectId.TryParse(applicationId, out var id)) return null;

        using var own = session is null ? await clientFactory.GetClient().StartSessionAsync() : null;
        var projected = await ProjectAsync(id, session ?? own!, default);

        return projected?.OrganisationId == organisationId ? projected : null;
    }

    public Task<AccreditationApplicationModel?> CreateAsync(
        AccreditationApplicationModel application
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task UpsertAsync(AccreditationApplicationModel application) =>
        throw new NotSupportedException("RA-706: not yet served from the model");

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

    public Task<AccreditationApplicationModel?> GetGrantedAccreditationAsync(
        int orgId,
        string registrationNumber,
        int year,
        int firstYear
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<(
        IReadOnlyList<AccreditationApplicationModel> Items,
        long Total
    )> SearchGrantedAccreditationsAsync(
        GrantedAccreditationFilter filter,
        int firstYear,
        int skip,
        int limit
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<UnservableApprovedCounts> CountUnservableApprovedAsync(
        int firstYear,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

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

    public Task<AccreditationApplicationModel?> UpdateIfInterimSiteNumberAbsentAsync(
        AccreditationApplicationModel application,
        string siteNumber,
        IClientSessionHandle? session = null
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

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

    public Task<AccreditationApplicationModel?> UpdateAsync(
        AccreditationApplicationModel application,
        IClientSessionHandle? session = null
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<AccreditationApplicationModel?> UpdateIfOrsIdAbsentAsync(
        AccreditationApplicationModel application,
        string orsId,
        IClientSessionHandle? session = null
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<AccreditationApplicationModel?> UpdateFieldsAsync(
        ObjectId id,
        FilterDefinition<AccreditationApplicationModel>? guardFilter,
        UpdateDefinition<AccreditationApplicationModel> update,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task StampDomainModelForeignKeysAsync(
        ObjectId applicationId,
        ObjectId registrationEntityId,
        IReadOnlyDictionary<string, ObjectId> overseasSiteEntityIds,
        ObjectId? accreditationEntityId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("RA-706: not yet served from the model");
}
