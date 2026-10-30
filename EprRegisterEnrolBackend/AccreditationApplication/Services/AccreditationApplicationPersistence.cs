using System.Linq.Expressions;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public class AccreditationApplicationPersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory
)
    : MongoService<AccreditationApplicationModel>(
        connectionFactory,
        "accreditationApplications",
        loggerFactory
    ),
        IAccreditationApplicationPersistence
{
    public async Task<AccreditationApplicationModel?> CreateAsync(
        AccreditationApplicationModel application
    )
    {
        try
        {
            await Collection.InsertOneAsync(application);
            return application;
        }
        catch (Exception e)
        {
            Logger.LogError(
                e,
                "Failed to insert accreditation application for org={OrganisationId}",
                application.OrganisationId
            );
            return null;
        }
    }

    public async Task UpsertAsync(AccreditationApplicationModel application) =>
        await Collection.ReplaceOneAsync(
            a => a.Id == application.Id,
            application,
            new ReplaceOptions { IsUpsert = true }
        );

    public async Task<long> DeleteManyAsync(IEnumerable<ObjectId> ids)
    {
        var idList = ids.ToList();
        var result = await Collection.DeleteManyAsync(a => idList.Contains(a.Id!.Value));
        return result.DeletedCount;
    }

    // RA-516: sorted server-side (CreatedAt desc, Id desc tiebreak) so callers no longer need to
    // re-sort in memory - backed by the compound index in DefineIndexes below. An unindexed Mongo
    // sort throws once the result set exceeds the 32MB in-memory sort limit, which is why this was
    // previously left unsorted and every caller sorted client-side instead (see the removed
    // AccreditationApplicationOrdering.NewestFirst() production usage).
    private static readonly SortDefinition<AccreditationApplicationModel> NewestFirstSort =
        Builders<AccreditationApplicationModel>
            .Sort.Descending(a => a.CreatedAt)
            .Descending(a => a.Id);

    public async Task<IEnumerable<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId
    )
    {
        return await Collection
            .Find(a => a.OrganisationId == organisationId)
            .Sort(NewestFirstSort)
            .ToListAsync();
    }

    public async Task<AccreditationApplicationModel?> GetLiveByRegistrationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year,
        IClientSessionHandle? session = null
    )
    {
        var filter = Builders<AccreditationApplicationModel>.Filter.And(
            Builders<AccreditationApplicationModel>.Filter.Eq(
                a => a.OrganisationId,
                organisationId
            ),
            Builders<AccreditationApplicationModel>.Filter.Eq(
                a => a.RegistrationId,
                registrationId
            ),
            Builders<AccreditationApplicationModel>.Filter.Eq(a => a.MaterialType, materialType),
            Builders<AccreditationApplicationModel>.Filter.Eq(a => a.Year, year),
            Builders<AccreditationApplicationModel>.Filter.Ne(
                a => a.ApplicationStatus,
                ApplicationStatus.Withdrawn
            )
        );

        return await Find(filter, session).Sort(NewestFirstSort).Limit(1).FirstOrDefaultAsync();
    }

    // RA-690: no dedicated index. The OrganisationId+MaterialType+Year compound index below
    // narrows this to one organisation's applications for one material, a handful of documents,
    // so the remaining filter and the sort run over almost nothing.
    public async Task<AccreditationApplicationModel?> GetLatestPreviousAccreditationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        GlassRecyclingProcess? glassRecyclingProcess,
        int beforeYear
    )
    {
        var filters = Builders<AccreditationApplicationModel>.Filter;
        // Eq on a null glass process also matches a document with no such field, which is how
        // non-glass applications are stored.
        var filter = filters.And(
            filters.Eq(a => a.OrganisationId, organisationId),
            filters.Eq(a => a.RegistrationId, registrationId),
            filters.Eq(a => a.MaterialType, materialType),
            filters.Eq(a => a.GlassRecyclingProcess, glassRecyclingProcess),
            filters.Lt(a => a.Year, beforeYear),
            filters.Eq(a => a.ApplicationStatus, ApplicationStatus.Approved),
            filters.Ne(a => a.AccreditationReference, null),
            filters.Ne(a => a.AccreditationReference, string.Empty)
        );

        return await Collection
            .Find(filter)
            .SortByDescending(a => a.Year)
            .ThenByDescending(a => a.DateLastEdited)
            .Limit(1)
            .FirstOrDefaultAsync();
    }

    // RA-690: no index yet, so this scans the collection. It runs once per approval, not per
    // read, so the scan is acceptable for now. The unique index on AccreditationReference that
    // follows RA-691 (once the counter-era references are migrated) will back it and replace
    // this check.
    public async Task<
        IReadOnlyList<AccreditationReferenceHolder>
    > GetOtherAccreditationReferenceHoldersAsync(
        string accreditationReference,
        ObjectId applicationId
    )
    {
        var filters = Builders<AccreditationApplicationModel>.Filter;
        var filter = filters.And(
            filters.Eq(a => a.AccreditationReference, accreditationReference),
            filters.Ne(a => a.Id, applicationId)
        );

        return await Collection
            .Find(filter)
            .Project(a => new AccreditationReferenceHolder(a.Id!.Value, a.ApplicationStatus))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<string>> GetOrsIdsByRegistrationAsync(
        string registrationId,
        IClientSessionHandle? session = null
    ) =>
        (await GetSitesByRegistrationAsync(registrationId, session))
            .Select(s => s.OrsId)
            .OfType<string>()
            .ToList();

    // RA-603: every interim site, withdrawn ones included - their numbers stay claimed for as
    // long as the record does, which under AC05 is permanently.
    public async Task<IReadOnlyList<string>> GetInterimSiteNumbersByRegistrationAsync(
        string registrationId,
        IClientSessionHandle? session = null
    ) =>
        (await GetSitesByRegistrationAsync(registrationId, session))
            .SelectMany(InterimSiteSync.All)
            .Select(i => i.SiteNumber)
            .OfType<string>()
            .ToList();

    // The overseas sites of every application under a registration - the allocation scope for
    // both ORS ids and interim site numbers. Projects to the overseas-sites section alone, since
    // this runs on every attempt of every add and the rest of the document is never read.
    private async Task<List<OverseasSiteModel>> GetSitesByRegistrationAsync(
        string registrationId,
        IClientSessionHandle? session
    )
    {
        var sections = await Find(
                Builders<AccreditationApplicationModel>.Filter.Eq(
                    a => a.RegistrationId,
                    registrationId
                ),
                session
            )
            .Project(a => a.OverseasSites)
            .ToListAsync();
        return sections.SelectMany(o => o?.Sites ?? []).ToList();
    }

    public async Task<AccreditationApplicationModel?> GetGrantedAccreditationAsync(
        int orgId,
        string registrationNumber,
        int year,
        int firstYear
    )
    {
        var filters = Builders<AccreditationApplicationModel>.Filter;
        var filter = filters.And(
            filters.Eq(a => a.OrgId, orgId),
            filters.Eq(a => a.RegistrationReference, registrationNumber),
            filters.Eq(a => a.Year, year),
            filters.Gte(a => a.Year, firstYear),
            filters.Eq(a => a.ApplicationStatus, ApplicationStatus.Approved),
            filters.Ne(a => a.AccreditationReference, null),
            filters.Ne(a => a.AccreditationReference, string.Empty)
        );

        return await Collection
            .Find(filter)
            .SortByDescending(a => a.DateLastEdited)
            .FirstOrDefaultAsync();
    }

    public async Task<(
        IReadOnlyList<AccreditationApplicationModel> Items,
        long Total
    )> SearchGrantedAccreditationsAsync(int? year, int firstYear, int skip, int limit)
    {
        var filters = Builders<AccreditationApplicationModel>.Filter;
        var filter = filters.And(
            filters.Ne(a => a.OrgId, null),
            filters.Gte(a => a.Year, firstYear),
            filters.Eq(a => a.ApplicationStatus, ApplicationStatus.Approved),
            filters.Ne(a => a.AccreditationReference, null),
            filters.Ne(a => a.AccreditationReference, string.Empty)
        );
        if (year is not null)
            filter = filters.And(filter, filters.Eq(a => a.Year, year.Value));

        var total = await Collection.CountDocumentsAsync(filter);
        var items = await Collection
            .Find(filter)
            .SortBy(a => a.AccreditationReference)
            .ThenBy(a => a.Id)
            .Skip(skip)
            .Limit(limit)
            .ToListAsync();
        return (items, total);
    }

    public async Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId,
        IClientSessionHandle? session = null
    )
    {
        if (!ObjectId.TryParse(applicationId, out var objectId))
            return null;

        return await Find(
                Builders<AccreditationApplicationModel>.Filter.Where(a =>
                    a.OrganisationId == organisationId && a.Id == objectId
                ),
                session
            )
            .FirstOrDefaultAsync();
    }

    public async Task<AccreditationApplicationModel?> GetByObjectIdAsync(
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    ) =>
        await Find(
                Builders<AccreditationApplicationModel>.Filter.Eq(a => a.Id, applicationId),
                session
            )
            .FirstOrDefaultAsync(cancellationToken);

    // Filters on OrganisationId as well as RegistrationId so the read uses the OrganisationId index:
    // RegistrationId has none of its own.
    public async Task<IReadOnlyList<AccreditationApplicationModel>> GetRegistrationGroupAsync(
        string organisationId,
        string registrationId,
        int limit,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        var filter = Builders<AccreditationApplicationModel>.Filter;
        return await Find(
                filter.Eq(a => a.OrganisationId, organisationId)
                    & filter.Eq(a => a.RegistrationId, registrationId),
                session
            )
            .Limit(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<AccreditationApplicationModel?> GetByCaseManagementWorkItemIdAsync(
        Guid workItemId,
        IClientSessionHandle? session = null
    )
    {
        return await Find(
                Builders<AccreditationApplicationModel>.Filter.Eq(
                    a => a.CaseManagementWorkItemId,
                    workItemId
                ),
                session
            )
            .FirstOrDefaultAsync();
    }

    public async Task<
        IReadOnlyList<AccreditationApplicationModel>
    > GetByAccreditationReferenceAsync(
        string accreditationReference,
        CancellationToken cancellationToken = default
    ) =>
        await Collection
            .Find(a => a.AccreditationReference == accreditationReference)
            .ToListAsync(cancellationToken);

    public Task<AccreditationApplicationModel?> UpdateAsync(
        AccreditationApplicationModel application,
        IClientSessionHandle? session = null
    )
    {
        if (application.Id is null)
            return Task.FromResult<AccreditationApplicationModel?>(null);

        var filter = Builders<AccreditationApplicationModel>.Filter.Eq(a => a.Id, application.Id);
        return ReplaceIfMatchAsync(application, filter, session);
    }

    public Task<AccreditationApplicationModel?> UpdateIfOrsIdAbsentAsync(
        AccreditationApplicationModel application,
        string orsId,
        IClientSessionHandle? session = null
    ) => UpdateIfNoSiteMatchesAsync(application, s => s.OrsId == orsId, session);

    // RA-603: a site number lives on an interim site nested in an overseas site. The legacy
    // singular mirror is checked as well as the list, matching the scope read above
    // (InterimSiteSync.All) - a number held only by an un-normalised pre-RA-603 document is
    // still taken.
    public Task<AccreditationApplicationModel?> UpdateIfInterimSiteNumberAbsentAsync(
        AccreditationApplicationModel application,
        string siteNumber,
        IClientSessionHandle? session = null
    ) =>
        UpdateIfNoSiteMatchesAsync(
            application,
            s =>
                s.InterimSites.Any(i => i.SiteNumber == siteNumber)
                || (s.InterimSite != null && s.InterimSite.SiteNumber == siteNumber),
            session
        );

    // Shared by the two allocation guards above: persist only if no overseas site on the stored
    // document already claims the value - i.e. a concurrent writer has not taken it first.
    private Task<AccreditationApplicationModel?> UpdateIfNoSiteMatchesAsync(
        AccreditationApplicationModel application,
        Expression<Func<OverseasSiteModel, bool>> claimsValue,
        IClientSessionHandle? session
    )
    {
        if (application.Id is null)
            return Task.FromResult<AccreditationApplicationModel?>(null);

        var filter = Builders<AccreditationApplicationModel>.Filter.And(
            Builders<AccreditationApplicationModel>.Filter.Eq(a => a.Id, application.Id),
            Builders<AccreditationApplicationModel>.Filter.Not(
                Builders<AccreditationApplicationModel>.Filter.ElemMatch(
                    a => a.OverseasSites!.Sites,
                    claimsValue
                )
            )
        );
        return ReplaceIfMatchAsync(application, filter, session);
    }

    // RA-482: shared by UpdateAsync and UpdateIfOrsIdAbsentAsync -- they differ only in the
    // filter (plain Id match vs. Id match plus an OrsId-absence guard), so the actual
    // stamp-then-replace-then-check-ModifiedCount body has exactly one implementation.
    //
    // RA-516: also guards against a lost update between two concurrent read-modify-write callers.
    // The filter additionally requires Version to still equal what the caller read; if a
    // concurrent writer already moved it on, this filter matches nothing, ModifiedCount is 0, and
    // - exactly like a not-found document today - the caller gets null back rather than silently
    // overwriting the other writer's change.
    //
    // Every AccreditationApplicationModel document written before this change has no "version"
    // field in storage at all (Mongo's equality filter never matches a genuinely absent field), so
    // an expected version of 0 - the in-memory default for both a brand-new document and one read
    // back from before this deploy - also accepts a document where the field is missing entirely.
    // That makes a document's first post-deploy update the point where "version" starts existing
    // in storage, with no separate backfill/migration step required.
    private async Task<AccreditationApplicationModel?> ReplaceIfMatchAsync(
        AccreditationApplicationModel application,
        FilterDefinition<AccreditationApplicationModel> filter,
        IClientSessionHandle? session
    )
    {
        var expectedVersion = application.Version;
        var versionedFilter = Builders<AccreditationApplicationModel>.Filter.And(
            filter,
            VersionFilter(expectedVersion)
        );

        application.UpdatedAt = DateTime.UtcNow;
        application.Version = expectedVersion + 1;
        var result = session is null
            ? await Collection.ReplaceOneAsync(versionedFilter, application)
            : await Collection.ReplaceOneAsync(session, versionedFilter, application);
        return result.ModifiedCount > 0 ? application : null;
    }

    // Shared by ReplaceIfMatchAsync above (its own expectedVersion is always the version it just
    // read) and available to any UpdateFieldsAsync caller that wants the same RA-516 optimistic-
    // concurrency semantics as a guard filter. Every AccreditationApplicationModel document
    // written before RA-516 shipped has no "version" field in storage at all (Mongo's equality
    // filter never matches a genuinely absent field), so an expected version of 0 - the in-memory
    // default for both a brand-new document and one read back from before this deploy - also
    // accepts a document where the field is missing entirely.
    private static FilterDefinition<AccreditationApplicationModel> VersionFilter(
        long expectedVersion
    ) =>
        expectedVersion == 0
            ? Builders<AccreditationApplicationModel>.Filter.Or(
                Builders<AccreditationApplicationModel>.Filter.Eq(a => a.Version, 0L),
                Builders<AccreditationApplicationModel>.Filter.Exists(a => a.Version, false)
            )
            : Builders<AccreditationApplicationModel>.Filter.Eq(a => a.Version, expectedVersion);

    // RA-519: the targeted-update counterpart to ReplaceIfMatchAsync above. Resubmit and Withdraw
    // both used to read-mutate-whole-document-replace, which raced with
    // StatusChangedFromCaseManagement's own read-mutate-whole-document-replace whenever
    // ManagementBe's synchronous push-back landed while one of those endpoints was still awaiting
    // its own outbound adapter call (RA-519 root cause) - two ReplaceOneAsync calls filtered only
    // by `_id` (or, after RA-516, `_id` + Version) each overwrite the *entire* document, so
    // whichever writer persists second wins outright and the first writer's fields are lost (or,
    // post-RA-516, the second writer's version filter no longer matches and it gets null back -
    // the 500 this fixes). A `$set`/`$push` update filtered only by `_id` only ever touches the
    // fields named in `update`, so two concurrent writers touching disjoint fields both survive
    // regardless of ordering.
    //
    // That guarantee is one-directional, not symmetric: it protects a targeted writer from being
    // clobbered by (or clobbering) a concurrent writer touching *different* fields. It does
    // nothing on its own to stop two callers racing to apply the *same* logical write twice - e.g.
    // two concurrent Resubmit requests, both reading ApplicationStatus == Queried before either
    // persists, both then $push-ing their own QuerySubmission. RA-516's plain Version filter can't
    // be reused as that guard either, since the very race this method exists to survive (the
    // Case Management service's own webhook write) also moves Version on - a Version-equality guard would
    // reject the legitimate second writer exactly as often as it rejects a genuine duplicate.
    // Callers that need to rule out a duplicate must pass a <paramref name="guardFilter"/> scoped
    // to whichever field(s) only a genuine rival writer (not the expected concurrent side-effect)
    // would have already changed - see Resubmit's and Withdraw's own call sites for the two
    // guards this repo currently needs. UpdatedAt and Version are stamped/incremented here so
    // every write path - whole-document replace or targeted update - keeps both current.
    public async Task<AccreditationApplicationModel?> UpdateFieldsAsync(
        ObjectId id,
        FilterDefinition<AccreditationApplicationModel>? guardFilter,
        UpdateDefinition<AccreditationApplicationModel> update,
        CancellationToken cancellationToken = default,
        IClientSessionHandle? session = null
    )
    {
        var idFilter = Builders<AccreditationApplicationModel>.Filter.Eq(a => a.Id, id);
        var filter = guardFilter is null
            ? idFilter
            : Builders<AccreditationApplicationModel>.Filter.And(idFilter, guardFilter);
        var combinedUpdate = Builders<AccreditationApplicationModel>.Update.Combine(
            update,
            Builders<AccreditationApplicationModel>.Update.Set(a => a.UpdatedAt, DateTime.UtcNow),
            Builders<AccreditationApplicationModel>.Update.Inc(a => a.Version, 1L)
        );

        var options = new FindOneAndUpdateOptions<AccreditationApplicationModel>
        {
            ReturnDocument = ReturnDocument.After,
        };
        return session is null
            ? await Collection.FindOneAndUpdateAsync(
                filter,
                combinedUpdate,
                options,
                cancellationToken
            )
            : await Collection.FindOneAndUpdateAsync(
                session,
                filter,
                combinedUpdate,
                options,
                cancellationToken
            );
    }

    // B8: inside a dual-write transaction every read and write takes the session, so it sees the
    // transaction's own writes and rolls back with it. Outside one the session is null and nothing changes.
    private IFindFluent<AccreditationApplicationModel, AccreditationApplicationModel> Find(
        FilterDefinition<AccreditationApplicationModel> filter,
        IClientSessionHandle? session
    ) => session is null ? Collection.Find(filter) : Collection.Find(session, filter);

    public async Task StampDomainModelForeignKeysAsync(
        ObjectId applicationId,
        ObjectId registrationEntityId,
        IReadOnlyDictionary<string, ObjectId> overseasSiteEntityIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default,
        ObjectId? accreditationEntityId = null
    )
    {
        var updates = new List<UpdateDefinition<AccreditationApplicationModel>>
        {
            Builders<AccreditationApplicationModel>.Update.Set(
                a => a.RegistrationEntityId,
                registrationEntityId
            ),
        };
        if (accreditationEntityId is not null)
        {
            updates.Add(
                Builders<AccreditationApplicationModel>.Update.Set(
                    a => a.AccreditationEntityId,
                    accreditationEntityId
                )
            );
        }
        var arrayFilters = new List<ArrayFilterDefinition>();
        // One array filter per site, named s0, s1, ... because an identifier must be a lowercase-led word.
        foreach (var (orsId, entityId) in overseasSiteEntityIds)
        {
            var name = $"s{arrayFilters.Count}";
            updates.Add(
                Builders<AccreditationApplicationModel>.Update.Set(
                    $"overseasSites.sites.$[{name}].overseasSiteEntityId",
                    entityId
                )
            );
            arrayFilters.Add(
                new BsonDocumentArrayFilterDefinition<BsonDocument>(
                    new BsonDocument($"{name}.orsId", orsId)
                )
            );
        }

        var result = await Collection.UpdateOneAsync(
            session,
            Builders<AccreditationApplicationModel>.Filter.Eq(a => a.Id, applicationId),
            Builders<AccreditationApplicationModel>.Update.Combine(updates),
            new UpdateOptions { ArrayFilters = arrayFilters },
            cancellationToken
        );
        if (result.MatchedCount == 0)
        {
            throw new InvalidOperationException(
                $"Application {applicationId} was not found to stamp its domain model foreign keys."
            );
        }
    }

    protected override List<CreateIndexModel<AccreditationApplicationModel>> DefineIndexes(
        IndexKeysDefinitionBuilder<AccreditationApplicationModel> builder
    )
    {
        return
        [
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Ascending(a => a.OrganisationId)
            ),
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Ascending(a => a.ApplicationStatus)
            ),
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Ascending(a => a.MaterialType)
            ),
            new CreateIndexModel<AccreditationApplicationModel>(builder.Ascending(a => a.Year)),
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Ascending(a => a.SourceReExAccreditationId)
            ),
            // Backs GetGrantedAccreditationAsync, called for every service client read of an
            // accreditation (organisation number, registration number and year).
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Combine(
                    builder.Ascending(a => a.OrgId),
                    builder.Ascending(a => a.RegistrationReference),
                    builder.Ascending(a => a.Year)
                )
            ),
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Combine(
                    builder.Ascending(a => a.OrganisationId),
                    builder.Ascending(a => a.MaterialType),
                    builder.Ascending(a => a.Year)
                )
            ),
            // RA-516: backs the server-side NewestFirstSort in GetByOrganisationAsync and
            // GetLiveByRegistrationAsync - without this, sorting by CreatedAt server-side would be
            // an unindexed sort, which Mongo refuses once the result set exceeds 32MB.
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Combine(
                    builder.Ascending(a => a.OrganisationId),
                    builder.Descending(a => a.CreatedAt),
                    builder.Descending(a => a.Id)
                )
            ),
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Ascending(a => a.ApplicationReference),
                new CreateIndexOptions { Unique = true, Sparse = true }
            ),
            // Backs GetByCaseManagementWorkItemIdAsync, called on every inbound Case Management
            // service query push —
            // without this the lookup is a full collection scan (RA-311 OBE-2).
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Ascending(a => a.CaseManagementWorkItemId),
                new CreateIndexOptions { Unique = true, Sparse = true }
            ),
        ];
    }
}
