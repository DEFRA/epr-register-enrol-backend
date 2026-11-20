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
    Func<CutFlush> cutFlush,
    ILogger<ModelBackedApplicationPersistence> logger
) : IAccreditationApplicationPersistence
{
    // The accreditation API's status name for an approved accreditation, as the legacy persistence compares it.
    private const string ApprovedStatusName = "approved";

    private async Task<T> RunInOwnTransactionAsync<T>(
        Func<IClientSessionHandle, CancellationToken, Task<T>> write,
        CancellationToken cancellationToken
    )
    {
        // Set by each attempt, so only the attempt that committed is logged.
        IReadOnlyList<MigrationConflict> conflicts = [];
        var committed = await InsertRaceRetry.RunInTransactionAsync(
            clientFactory,
            async (session, token) =>
            {
                var flush = cutFlush();
                flush.Begin(session);
                var result = await write(session, token);
                conflicts = await flush.FlushAsync(session, token);
                return result;
            },
            attempt =>
                logger.LogWarning(
                    "Model-backed write lost an insert race and will retry: attempt {Attempt}",
                    attempt
                ),
            cancellationToken
        );
        logger.LogRecorded(conflicts);
        return committed;
    }

    // maxTime limits the lookup query (RA-702); the per-application projection reads aren't limited yet: batching
    // them, before the cut goes live, is where the limit covers them too.
    private async Task<List<AccreditationApplicationModel>> ProjectCurrentAsync(
        FilterDefinition<ApplicationEntity> envelope,
        IClientSessionHandle? session,
        CancellationToken ct,
        TimeSpan? maxTime = null
    )
    {
        using var ownSession = session is null
            ? await clientFactory.GetClient().StartSessionAsync(cancellationToken: ct)
            : null;
        var readSession = session ?? ownSession!;

        var candidates = await stores.Applications.Versions.FindAllCurrentAsync(
            envelope & ApplicationFields.Accreditations,
            readSession,
            ct,
            maxTime
        );

        var projected = new List<AccreditationApplicationModel>();
        foreach (var candidate in candidates)
        {
            // Inside a caller's transaction a staged copy is the current state.
            var application =
                session != null
                    ? await ReadAsync(candidate.EntityId, readSession, ct)
                    : await ProjectAsync(candidate.EntityId, readSession, ct);
            if (application != null)
            {
                projected.Add(application);
            }
        }

        return projected;
    }

    private async Task<AccreditationApplicationModel?> ReadAsync(
        ObjectId id,
        IClientSessionHandle session,
        CancellationToken ct
    )
    {
        return await staging.GetByObjectIdAsync(id, session, ct)
            ?? await ProjectAsync(id, session, ct);
    }

    private async Task EnsureStagedAsync(
        ObjectId id,
        IClientSessionHandle session,
        CancellationToken ct
    )
    {
        if (await staging.GetByObjectIdAsync(id, session, ct) is null)
        {
            var projected = await ProjectAsync(id, session, ct, forWrite: true);
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
        if (session != null && session.IsInTransaction)
        {
            return session;
        }

        throw new NotSupportedException(
            "RA-706: a write with a session must run in its transaction"
        );
    }

    // A write to one application: inside the caller's transaction it edits the staged copy (staging it first); with no
    // session it runs in a transaction of its own, which flushes.
    private async Task<T> WriteStagedAsync<T>(
        ObjectId id,
        IClientSessionHandle? session,
        Func<IClientSessionHandle, CancellationToken, Task<T>> write,
        CancellationToken ct
    )
    {
        if (session is null)
        {
            return await RunInOwnTransactionAsync(
                (own, token) => WriteStagedAsync(id, own, write, token),
                ct
            );
        }
        var transaction = RequireTransaction(session);
        await EnsureStagedAsync(id, transaction, ct);
        return await write(transaction, ct);
    }

    private static FilterDefinition<ApplicationEntity> OnRegistration(
        string organisationId,
        string registrationId
    ) =>
        Builders<ApplicationEntity>.Filter.Eq(a => a.ReExOrganisationId, organisationId)
        & Builders<ApplicationEntity>.Filter.Eq(a => a.ReExRegistrationId, registrationId);

    // The organisation's applications, found by its number; null when the model has no such organisation.
    private async Task<FilterDefinition<ApplicationEntity>?> OfOrganisationNumberAsync(
        int organisationNumber,
        CancellationToken ct
    )
    {
        var organisation = await stores.Organisations.FindByOrganisationNumberAsync(
            organisationNumber,
            null,
            ct
        );
        return organisation is null
            ? null
            : Builders<ApplicationEntity>.Filter.Eq(
                a => a.ReExOrganisationId,
                organisation.ReExOrganisationId
            );
    }

    private static FilterDefinition<ApplicationEntity> OnRegistrationOnly(string registrationId) =>
        Builders<ApplicationEntity>.Filter.Eq(a => a.ReExRegistrationId, registrationId);

    private static IEnumerable<OverseasSiteModel> SitesOf(
        IEnumerable<AccreditationApplicationModel> applications
    ) => applications.SelectMany(a => a.OverseasSites?.Sites ?? []);

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    public async Task<
        IReadOnlyList<AccreditationApplicationModel>
    > GetByAccreditationReferenceAsync(
        string accreditationReference,
        CancellationToken cancellationToken = default
    )
    {
        var accreditations = await stores.Accreditations.Versions.FindAllCurrentAsync(
            Builders<AccreditationEntity>.Filter.Eq(
                a => a.AccreditationNumber,
                accreditationReference
            ),
            null,
            cancellationToken
        );
        var accreditationIds = accreditations.Select(a => a.EntityId).ToList();
        var envelope =
            Builders<ApplicationEntity>.Filter.Eq(
                ApplicationFields.ReservedAccreditationNumber,
                accreditationReference
            )
            | Builders<ApplicationEntity>.Filter.In(
                ApplicationFields.AccreditationId,
                accreditationIds
            );
        return (await ProjectCurrentAsync(envelope, null, cancellationToken))
            .Where(a => a.AccreditationReference == accreditationReference)
            .ToList();
    }

    // forWrite: the projection is about to be staged, and the sync stores whatever createdAt it carries.
    private async Task<AccreditationApplicationModel?> ProjectAsync(
        ObjectId id,
        IClientSessionHandle session,
        CancellationToken ct,
        bool forWrite = false
    )
    {
        var sources = await readModel.LoadAsync(id, session, ct);
        if (sources is null)
        {
            return null;
        }

        var legacy = ApplicationProjection.ToLegacy(sources);

        legacy.Version = sources.Application.Version;
        legacy.CreatedAt =
            sources.Application.CreatedAt ?? MissedByTheBackfill(sources.Application, forWrite);
        legacy.UpdatedAt = sources.Application.ValidFrom;

        legacy.RegistrationEntityId = sources.Application.RegistrationId;
        legacy.AccreditationEntityId = (
            sources.Application.Details as AccreditationDetails
            ?? throw new InvalidOperationException(
                $"Application {id} is not an accreditation application: its details are {sources.Application.Details.GetType().Name}."
            )
        ).AccreditationId;

        return legacy;
    }

    // Decision 5: the backfill fills createdAt on every version written before the model held it, and readiness
    // check A lists any it missed. The legacy shape needs a time, so a missed one gets when the current version was
    // written: loudly, since it moves the application in the GET list's order. A write refuses instead: it would store
    // that made-up time as the application's createdAt, for good.
    private DateTime MissedByTheBackfill(ApplicationEntity application, bool forWrite)
    {
        if (forWrite)
        {
            throw new InvalidOperationException(
                $"Application {application.EntityId} has no createdAt in the model (missed by the backfill), so it cannot be written: run the createdAt backfill until cut-readiness check A lists nothing."
            );
        }

        logger.LogWarning(
            "Application {ApplicationId} has no createdAt in the model (missed by the backfill); serving its current version's validFrom {ValidFrom} instead",
            application.EntityId,
            application.ValidFrom
        );
        return application.ValidFrom;
    }

    public Task<AccreditationApplicationModel?> GetByObjectIdAsync(
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    ) => ReadAsync(applicationId, session, cancellationToken);

    public async Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId,
        IClientSessionHandle? session = null
    )
    {
        if (!ObjectId.TryParse(applicationId, out var id))
        {
            return null;
        }

        using var ownSession = session is null
            ? await clientFactory.GetClient().StartSessionAsync()
            : null;
        var projected = await ReadAsync(id, session ?? ownSession!, default);

        return projected?.OrganisationId == organisationId ? projected : null;
    }

    public async Task<AccreditationApplicationModel?> CreateAsync(
        AccreditationApplicationModel application
    )
    {
        try
        {
            return await RunInOwnTransactionAsync(
                async (own, token) =>
                {
                    await staging.StageAsync(application, own, token);
                    return application;
                },
                CancellationToken.None
            );
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(
                e,
                "Failed to insert accreditation application for org={OrganisationId}",
                application.OrganisationId
            );
            return null;
        }
    }

    public async Task UpsertAsync(AccreditationApplicationModel application)
    {
        await RunInOwnTransactionAsync(
            async (own, token) =>
            {
                await staging.DeleteStagedAsync([application.Id!.Value], own, token);
                await staging.StageAsync(application, own, token);
                return true;
            },
            CancellationToken.None
        );
    }

    public async Task<long> DeleteManyAsync(IEnumerable<ObjectId> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return 0;
        }

        return await RunInOwnTransactionAsync(
            async (session, token) =>
            {
                var current = await stores.Applications.Versions.FindAllCurrentAsync(
                    Builders<ApplicationEntity>.Filter.In(x => x.EntityId, idList),
                    session,
                    token
                );

                if (current.Count == 0)
                {
                    return 0L;
                }

                await stores.Accreditations.Versions.DeleteAllVersionsAsync(
                    Builders<AccreditationEntity>.Filter.In(
                        a => a.ApplicationId,
                        idList.Select(id => (ObjectId?)id)
                    ),
                    session,
                    token
                );

                await stores.Applications.Versions.DeleteAllVersionsAsync(
                    Builders<ApplicationEntity>.Filter.In(a => a.EntityId, idList),
                    session,
                    token
                );

                return (long)current.Count;
            },
            CancellationToken.None
        );
    }

    public async Task<IEnumerable<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId
    )
    {
        var envelope = Builders<ApplicationEntity>.Filter.Eq(
            a => a.ReExOrganisationId,
            organisationId
        );
        var results = await ProjectCurrentAsync(envelope, null, CancellationToken.None);
        return results.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).ToList();
    }

    public async Task<AccreditationApplicationModel?> GetLiveByRegistrationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year,
        IClientSessionHandle? session = null
    )
    {
        var envelope = OnRegistration(organisationId, registrationId);

        var results = await ProjectCurrentAsync(envelope, session, CancellationToken.None);
        return results
            .Where(a =>
                a.MaterialType == materialType
                && a.Year == year
                && a.ApplicationStatus != ApplicationStatus.Withdrawn
            )
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .FirstOrDefault();
    }

    public async Task<AccreditationApplicationModel?> GetLatestPreviousAccreditationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int beforeYear
    )
    {
        var envelope = OnRegistration(organisationId, registrationId);

        var results = await ProjectCurrentAsync(envelope, null, CancellationToken.None);
        return results
            .Where(a =>
                a.MaterialType == materialType
                && a.Year < beforeYear
                && a.ApplicationStatus == ApplicationStatus.Approved
                // As legacy's query: null or empty, not blank, unlike the accreditation API's lookups.
                && !string.IsNullOrEmpty(a.AccreditationReference)
            )
            .OrderByDescending(a => a.Year)
            .ThenByDescending(a => a.DateLastEdited)
            .FirstOrDefault();
    }

    public async Task<
        IReadOnlyList<AccreditationReferenceHolder>
    > GetOtherAccreditationReferenceHoldersAsync(
        string accreditationReference,
        ObjectId applicationId
    )
    {
        var holders = await GetByAccreditationReferenceAsync(
            accreditationReference,
            CancellationToken.None
        );
        return holders
            .Where(a => a.Id != applicationId)
            .Select(a => new AccreditationReferenceHolder(a.Id!.Value, a.ApplicationStatus))
            .ToList();
    }

    public async Task<AccreditationApplicationModel?> GetGrantedAccreditationAsync(
        int orgId,
        string registrationNumber,
        int year,
        int firstYear,
        TimeSpan? maxTime = null
    )
    {
        var ofOrganisation = await OfOrganisationNumberAsync(orgId, CancellationToken.None);
        if (ofOrganisation is null)
        {
            return null;
        }

        var envelope =
            Builders<ApplicationEntity>.Filter.Eq(a => a.Status, ApplicationStatus.Approved)
            & ofOrganisation;

        var results = await ProjectCurrentAsync(
            envelope,
            null,
            CancellationToken.None,
            maxTime
        );

        return results
            .Where(a =>
                a.OrgId == orgId
                && a.RegistrationReference == registrationNumber
                && a.Year == year
                && a.Year >= firstYear
                && a.ApplicationStatus == ApplicationStatus.Approved
                && !IsBlank(a.AccreditationReference)
            )
            .OrderByDescending(a => a.DateLastEdited)
            .FirstOrDefault();
    }

    public async Task<(
        IReadOnlyList<AccreditationApplicationModel> Items,
        long Total
    )> SearchGrantedAccreditationsAsync(
        GrantedAccreditationFilter filter,
        int firstYear,
        int skip,
        int limit,
        TimeSpan? maxTime = null
    )
    {
        // RA-702, as legacy: every accreditation served is approved, so a search for any other status finds none.
        if (filter.Status is not null && filter.Status != ApprovedStatusName)
        {
            return ([], 0);
        }

        var envelope = Builders<ApplicationEntity>.Filter.Eq(
            a => a.Status,
            ApplicationStatus.Approved
        );
        if (filter.OrganisationNumber.HasValue)
        {
            var ofOrganisation = await OfOrganisationNumberAsync(
                filter.OrganisationNumber.Value,
                CancellationToken.None
            );
            if (ofOrganisation is null)
            {
                return ([], 0);
            }
            envelope &= ofOrganisation;
        }

        var results = await ProjectCurrentAsync(
            envelope,
            null,
            CancellationToken.None,
            maxTime
        );

        var granted = results
            .Where(a =>
                a.OrgId != null
                && a.Year >= firstYear
                && a.ApplicationStatus == ApplicationStatus.Approved
                && !IsBlank(a.AccreditationReference)
                && !IsBlank(a.RegistrationReference)
                && (!filter.Year.HasValue || a.Year == filter.Year)
                && (!filter.OrganisationNumber.HasValue || a.OrgId == filter.OrganisationNumber)
                && (
                    filter.RegistrationNumber is null
                    || a.RegistrationReference == filter.RegistrationNumber
                )
            )
            .ToList();

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
        var envelope = Builders<ApplicationEntity>.Filter.Eq(
            a => a.Status,
            ApplicationStatus.Approved
        );
        var results = await ProjectCurrentAsync(envelope, null, cancellationToken);

        var approved = results
            .Where(a => a.ApplicationStatus == ApplicationStatus.Approved && a.Year >= firstYear)
            .ToList();

        return new UnservableApprovedCounts(
            approved.Count(a => a.OrgId == null),
            approved.Count(a => IsBlank(a.RegistrationReference)),
            approved.Count(a => IsBlank(a.AccreditationReference))
        );
    }

    public async Task<IReadOnlyList<string>> GetOrsIdsByRegistrationAsync(
        string registrationId,
        IClientSessionHandle? session = null
    )
    {
        var results = await ProjectCurrentAsync(
            OnRegistrationOnly(registrationId),
            session,
            CancellationToken.None
        );
        return SitesOf(results)
            .Select(s => s.OrsId)
            .OfType<string>()
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetInterimSiteNumbersByRegistrationAsync(
        string registrationId,
        IClientSessionHandle? session = null
    )
    {
        var results = await ProjectCurrentAsync(
            OnRegistrationOnly(registrationId),
            session,
            CancellationToken.None
        );
        return SitesOf(results)
            .SelectMany(InterimSiteSync.All)
            .Select(i => i.SiteNumber)
            .OfType<string>()
            .ToList();
    }

    public async Task<AccreditationApplicationModel?> UpdateIfInterimSiteNumberAbsentAsync(
        AccreditationApplicationModel application,
        string siteNumber,
        IClientSessionHandle? session = null
    )
    {
        return await WriteStagedAsync(
            application.Id!.Value,
            session,
            (transaction, _) =>
                staging.UpdateIfInterimSiteNumberAbsentAsync(application, siteNumber, transaction),
            CancellationToken.None
        );
    }

    public async Task<AccreditationApplicationModel?> GetNewestForOrganisationAsync(
        string organisationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        var envelope = Builders<ApplicationEntity>.Filter.Eq(
            a => a.ReExOrganisationId,
            organisationId
        );
        var results = await ProjectCurrentAsync(envelope, session, cancellationToken);
        return results
            .OrderByDescending(a => a.UpdatedAt)
            .ThenByDescending(a => a.Id)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<AccreditationApplicationModel>> GetRegistrationGroupAsync(
        string organisationId,
        string registrationId,
        int limit,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        var envelope = OnRegistration(organisationId, registrationId);

        var results = await ProjectCurrentAsync(envelope, session, cancellationToken);

        if (session != null)
        {
            foreach (var id in staging.StagedIds(session))
            {
                if (results.Any(a => a.Id == id))
                {
                    continue;
                }

                var staged = await staging.GetByObjectIdAsync(id, session, cancellationToken);
                if (
                    staged is not null
                    && staged.OrganisationId == organisationId
                    && staged.RegistrationId == registrationId
                )
                {
                    results.Add(staged);
                }
            }
        }

        return results.Take(limit).ToList();
    }

    public async Task<AccreditationApplicationModel?> GetByCaseManagementWorkItemIdAsync(
        Guid workItemId,
        IClientSessionHandle? session = null
    )
    {
        var envelope = Builders<ApplicationEntity>.Filter.Eq(
            a => a.CaseManagementWorkItemId,
            workItemId
        );
        var results = await ProjectCurrentAsync(envelope, session, CancellationToken.None);
        return results.FirstOrDefault(a => a.CaseManagementWorkItemId == workItemId);
    }

    // RA-794's isNewSite audit and correction, as legacy's query: every application created before the RA-292 deploy
    // that holds an overseas site, in id order. createdAt has been the model's own Application field since v6.14
    // (the backfill copies legacy's), and isNewSite, orsId and previousSites, which the classifier reads, are all
    // projected. A current version the backfill missed has no createdAt to place it inside or outside the window, so
    // the lookup refuses, as a write to one does, rather than leave a wrongly-defaulted site uncorrected unseen.
    public async Task<IReadOnlyList<AccreditationApplicationModel>> GetCreatedBeforeWithOverseasSitesAsync(
        DateTime createdBefore,
        CancellationToken cancellationToken = default
    )
    {
        var missed = await stores.Applications.Versions.FindAllCurrentAsync(
            Builders<ApplicationEntity>.Filter.Eq(a => a.CreatedAt, null) & ApplicationFields.Accreditations,
            cancellationToken: cancellationToken
        );
        if (missed.Count > 0)
        {
            throw new InvalidOperationException(
                $"Applications {string.Join(", ", missed.Select(m => m.EntityId))} have no createdAt in the model (missed by the backfill), so the RA-794 window cannot be applied to them: run the createdAt backfill until cut-readiness check A lists nothing."
            );
        }

        var envelope = Builders<ApplicationEntity>.Filter.Lt(a => a.CreatedAt, createdBefore);
        var results = await ProjectCurrentAsync(envelope, null, cancellationToken);
        return results
            .Where(a => a.CreatedAt < createdBefore && a.OverseasSites?.Sites.Count > 0)
            .OrderBy(a => a.Id)
            .ToList();
    }

    // RA-766's charge correction, as legacy's query: every open application with a work item, Queried left out.
    public async Task<
        IReadOnlyList<AccreditationApplicationModel>
    > GetOpenNotQueriedWithCaseManagementWorkItemAsync(
        CancellationToken cancellationToken = default
    )
    {
        var excluded = AccreditationApplicationPersistence.ExcludedStatuses;
        var envelope =
            Builders<ApplicationEntity>.Filter.Ne(a => a.CaseManagementWorkItemId, null)
            & Builders<ApplicationEntity>.Filter.Nin(a => a.Status, excluded);
        var results = await ProjectCurrentAsync(envelope, null, cancellationToken);
        return results
            .Where(a =>
                a.CaseManagementWorkItemId != null && !excluded.Contains(a.ApplicationStatus)
            )
            .OrderBy(a => a.Id)
            .ToList();
    }

    public async Task<IReadOnlyList<AccreditationApplicationModel>> GetByOrgIdAndYearAsync(
        int orgId,
        int year,
        CancellationToken cancellationToken = default
    )
    {
        var ofOrganisation = await OfOrganisationNumberAsync(orgId, cancellationToken);
        if (ofOrganisation is null)
        {
            return [];
        }

        var results = await ProjectCurrentAsync(ofOrganisation, null, cancellationToken);
        return results.Where(a => a.OrgId == orgId && a.Year == year).OrderBy(a => a.Id).ToList();
    }

    // RA-764 one-off: the registration reference is not held on the application entity, so this
    // projects every current application and filters, as the one startup call it backs allows.
    public async Task<
        IReadOnlyList<AccreditationApplicationModel>
    > GetByRegistrationReferencesAndYearAsync(
        IReadOnlyCollection<string> registrationReferences,
        int year,
        CancellationToken cancellationToken = default
    )
    {
        var results = await ProjectCurrentAsync(
            Builders<ApplicationEntity>.Filter.Empty,
            null,
            cancellationToken
        );
        return results
            .Where(a =>
                a.Year == year
                && a.RegistrationReference is { } reference
                && registrationReferences.Contains(reference)
            )
            .OrderBy(a => a.Id)
            .ToList();
    }

    public async Task<IReadOnlyList<AccreditationApplicationModel>> GetByYearAsync(
        int year,
        CancellationToken cancellationToken = default
    )
    {
        var results = await ProjectCurrentAsync(
            Builders<ApplicationEntity>.Filter.Empty,
            null,
            cancellationToken
        );
        return results.Where(a => a.Year == year).OrderBy(a => a.Id).ToList();
    }

    public async Task<AccreditationApplicationModel?> UpdateAsync(
        AccreditationApplicationModel application,
        IClientSessionHandle? session = null
    )
    {
        return await WriteStagedAsync(
            application.Id!.Value,
            session,
            (transaction, _) => staging.UpdateAsync(application, transaction),
            CancellationToken.None
        );
    }

    public async Task<AccreditationApplicationModel?> UpdateIfOrsIdAbsentAsync(
        AccreditationApplicationModel application,
        string orsId,
        IClientSessionHandle? session = null
    )
    {
        return await WriteStagedAsync(
            application.Id!.Value,
            session,
            (transaction, _) => staging.UpdateIfOrsIdAbsentAsync(application, orsId, transaction),
            CancellationToken.None
        );
    }

    public async Task<AccreditationApplicationModel?> UpdateFieldsAsync(
        ObjectId id,
        FilterDefinition<AccreditationApplicationModel>? guardFilter,
        UpdateDefinition<AccreditationApplicationModel> update,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    )
    {
        return await WriteStagedAsync(
            id,
            session,
            (transaction, token) =>
                staging.UpdateFieldsAsync(id, guardFilter, update, transaction, token),
            cancellationToken
        );
    }

    public async Task StampDomainModelForeignKeysAsync(
        ObjectId applicationId,
        ObjectId registrationEntityId,
        IReadOnlyDictionary<string, ObjectId> overseasSiteEntityIds,
        ObjectId? accreditationEntityId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        if (await staging.GetByObjectIdAsync(applicationId, session, cancellationToken) is null)
        {
            return;
        }
        await staging.StampDomainModelForeignKeysAsync(
            applicationId,
            registrationEntityId,
            overseasSiteEntityIds,
            accreditationEntityId,
            session,
            cancellationToken
        );
    }
}
