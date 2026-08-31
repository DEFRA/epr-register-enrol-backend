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
        int year
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

        return await Collection.Find(filter).Sort(NewestFirstSort).Limit(1).FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<string>> GetOrsIdsByRegistrationAsync(string registrationId)
    {
        var filter = Builders<AccreditationApplicationModel>.Filter.Eq(
            a => a.RegistrationId,
            registrationId
        );

        var applications = await Collection.Find(filter).ToListAsync();
        return applications
            .SelectMany(a => a.OverseasSites?.Sites ?? [])
            .Select(s => s.OrsId)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToList();
    }

    public async Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId
    )
    {
        if (!ObjectId.TryParse(applicationId, out var objectId))
            return null;

        return await Collection
            .Find(a => a.OrganisationId == organisationId && a.Id == objectId)
            .FirstOrDefaultAsync();
    }

    public async Task<AccreditationApplicationModel?> GetByCaseManagementWorkItemIdAsync(
        Guid workItemId
    )
    {
        return await Collection
            .Find(a => a.CaseManagementWorkItemId == workItemId)
            .FirstOrDefaultAsync();
    }

    public Task<AccreditationApplicationModel?> UpdateAsync(
        AccreditationApplicationModel application
    )
    {
        if (application.Id is null)
            return Task.FromResult<AccreditationApplicationModel?>(null);

        var filter = Builders<AccreditationApplicationModel>.Filter.Eq(a => a.Id, application.Id);
        return ReplaceIfMatchAsync(application, filter);
    }

    public Task<AccreditationApplicationModel?> UpdateIfOrsIdAbsentAsync(
        AccreditationApplicationModel application,
        string orsId
    )
    {
        if (application.Id is null)
            return Task.FromResult<AccreditationApplicationModel?>(null);

        var filter = Builders<AccreditationApplicationModel>.Filter.And(
            Builders<AccreditationApplicationModel>.Filter.Eq(a => a.Id, application.Id),
            Builders<AccreditationApplicationModel>.Filter.Not(
                Builders<AccreditationApplicationModel>.Filter.ElemMatch(
                    a => a.OverseasSites!.Sites,
                    s => s.OrsId == orsId
                )
            )
        );
        return ReplaceIfMatchAsync(application, filter);
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
        FilterDefinition<AccreditationApplicationModel> filter
    )
    {
        var expectedVersion = application.Version;
        var versionedFilter = Builders<AccreditationApplicationModel>.Filter.And(
            filter,
            VersionFilter(expectedVersion)
        );

        application.UpdatedAt = DateTime.UtcNow;
        application.Version = expectedVersion + 1;
        var result = await Collection.ReplaceOneAsync(versionedFilter, application);
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
        CancellationToken cancellationToken = default
    )
    {
        var idFilter = Builders<AccreditationApplicationModel>.Filter.Eq(a => a.Id, id);
        var filter =
            guardFilter is null
                ? idFilter
                : Builders<AccreditationApplicationModel>.Filter.And(idFilter, guardFilter);
        var combinedUpdate = Builders<AccreditationApplicationModel>.Update.Combine(
            update,
            Builders<AccreditationApplicationModel>.Update.Set(a => a.UpdatedAt, DateTime.UtcNow),
            Builders<AccreditationApplicationModel>.Update.Inc(a => a.Version, 1L)
        );

        return await Collection.FindOneAndUpdateAsync(
            filter,
            combinedUpdate,
            new FindOneAndUpdateOptions<AccreditationApplicationModel>
            {
                ReturnDocument = ReturnDocument.After,
            },
            cancellationToken
        );
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
