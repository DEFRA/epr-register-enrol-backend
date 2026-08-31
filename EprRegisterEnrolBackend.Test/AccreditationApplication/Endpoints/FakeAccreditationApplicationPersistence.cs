using EprRegisterEnrolBackend.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

public class FakeAccreditationApplicationPersistence : IAccreditationApplicationPersistence
{
    private readonly List<AccreditationApplicationModel> _store = [];

    public void Seed(AccreditationApplicationModel application) => _store.Add(application);

    public void Clear()
    {
        _store.Clear();
        FailNextUpdate = false;
        FailNextOrsIdWrites = 0;
    }

    /// <summary>
    /// When set, the next call to <see cref="UpdateAsync"/> returns null (as if the persisted
    /// record vanished between the read and the write) and clears itself, so endpoints'
    /// `updated is null ? Results.Problem(...) : ...` branches can be exercised without a real
    /// database. Purely additive test-only infrastructure.
    /// </summary>
    public bool FailNextUpdate { get; set; }

    /// <summary>
    /// RA-482: when greater than zero, the next N calls to <see cref="UpdateIfOrsIdAbsentAsync"/>
    /// return null (as if a concurrent writer had already claimed that OrsId) and decrement this
    /// counter, so AddOverseasSite's retry-on-conflict loop can be exercised deterministically
    /// without real concurrency.
    /// </summary>
    public int FailNextOrsIdWrites { get; set; }

    public Task<AccreditationApplicationModel?> CreateAsync(
        AccreditationApplicationModel application
    )
    {
        if (application.Id is null || application.Id == ObjectId.Empty)
            application.Id = ObjectId.GenerateNewId();
        _store.Add(application);
        return Task.FromResult<AccreditationApplicationModel?>(application);
    }

    // RA-516: mirrors AccreditationApplicationPersistence.GetByOrganisationAsync's server-side
    // newest-first sort, using the same shared AccreditationApplicationOrdering.NewestFirst() rule
    // the real class's compound index now enforces, so this fake stays a faithful stand-in.
    public Task<IEnumerable<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId
    ) =>
        Task.FromResult<IEnumerable<AccreditationApplicationModel>>(
            _store.Where(a => a.OrganisationId == organisationId).NewestFirst().ToList()
        );

    public Task<AccreditationApplicationModel?> GetLiveByRegistrationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year
    )
    {
        var match = _store
            .Where(a =>
                a.OrganisationId == organisationId
                && a.RegistrationId == registrationId
                && a.MaterialType == materialType
                && a.Year == year
                && a.ApplicationStatus != ApplicationStatus.Withdrawn
            )
            .NewestFirst()
            .FirstOrDefault();
        return Task.FromResult(match is null ? null : ShallowCopy(match));
    }

    public Task<IReadOnlyList<string>> GetOrsIdsByRegistrationAsync(string registrationId)
    {
        IReadOnlyList<string> orsIds = _store
            .Where(a => a.RegistrationId == registrationId)
            .SelectMany(a => a.OverseasSites?.Sites ?? [])
            .Select(s => s.OrsId)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToList();
        return Task.FromResult(orsIds);
    }

    public Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId
    )
    {
        if (!ObjectId.TryParse(applicationId, out var oid))
            return Task.FromResult<AccreditationApplicationModel?>(null);
        var stored = _store.FirstOrDefault(a => a.OrganisationId == organisationId && a.Id == oid);
        // Shallow copy so endpoint mutations to top-level value fields (ApplicationStatus, ApplicationReference, etc.)
        // don't affect the stored record unless UpdateAsync is explicitly called.
        return Task.FromResult(stored is null ? null : ShallowCopy(stored));
    }

    public Task<AccreditationApplicationModel?> GetByCaseManagementWorkItemIdAsync(Guid workItemId)
    {
        var stored = _store.FirstOrDefault(a => a.CaseManagementWorkItemId == workItemId);
        return Task.FromResult(stored is null ? null : ShallowCopy(stored));
    }

    public Task<AccreditationApplicationModel?> UpdateAsync(
        AccreditationApplicationModel application
    )
    {
        if (FailNextUpdate)
        {
            FailNextUpdate = false;
            return Task.FromResult<AccreditationApplicationModel?>(null);
        }

        var idx = _store.FindIndex(a => a.Id == application.Id);
        if (idx < 0)
            return Task.FromResult<AccreditationApplicationModel?>(null);
        // RA-516: mirrors AccreditationApplicationPersistence.ReplaceIfMatchAsync's optimistic
        // concurrency check.
        if (_store[idx].Version != application.Version)
            return Task.FromResult<AccreditationApplicationModel?>(null);
        application.Version++;
        _store[idx] = application;
        return Task.FromResult<AccreditationApplicationModel?>(application);
    }

    public Task<AccreditationApplicationModel?> UpdateIfOrsIdAbsentAsync(
        AccreditationApplicationModel application,
        string orsId
    )
    {
        if (FailNextOrsIdWrites > 0)
        {
            FailNextOrsIdWrites--;
            return Task.FromResult<AccreditationApplicationModel?>(null);
        }

        var idx = _store.FindIndex(a => a.Id == application.Id);
        if (idx < 0)
            return Task.FromResult<AccreditationApplicationModel?>(null);

        var alreadyPresent = (_store[idx].OverseasSites?.Sites ?? []).Any(s => s.OrsId == orsId);
        if (alreadyPresent)
            return Task.FromResult<AccreditationApplicationModel?>(null);

        // RA-516: mirrors AccreditationApplicationPersistence.ReplaceIfMatchAsync's optimistic
        // concurrency check.
        if (_store[idx].Version != application.Version)
            return Task.FromResult<AccreditationApplicationModel?>(null);
        application.Version++;
        _store[idx] = application;
        return Task.FromResult<AccreditationApplicationModel?>(application);
    }

    /// <summary>
    /// RA-519: real field-level merge for <see cref="IAccreditationApplicationPersistence.UpdateFieldsAsync"/>,
    /// so tests exercising it through this fake actually distinguish "both concurrent writers'
    /// changes survived" from "one clobbered the other" - the thing a naive last-write-wins fake
    /// couldn't tell apart. Renders the UpdateDefinition to the same $set/$push/$inc BsonDocument
    /// shape the real Mongo driver would send over the wire (via the model's own class-map
    /// serializer, so element names/representations match production exactly), then applies each
    /// operator onto a BSON-serialized copy of the stored record and deserializes the result back -
    /// deliberately not attempting to interpret arbitrary UpdateDefinition shapes generically,
    /// just the $set/$push/$inc operators AccreditationApplicationSections' Build*Update helpers
    /// and UpdateFieldsAsync itself ever produce.
    ///
    /// <paramref name="guardFilter"/>, when supplied, is rendered the same way and evaluated
    /// against the stored document with <see cref="MatchesFilter"/> - again deliberately scoped to
    /// just the operators Resubmit's/Withdraw's own guard filters actually produce ($ne, $not +
    /// $size), rather than a general-purpose filter evaluator. A non-matching guard mirrors a real
    /// FindOneAndUpdateAsync call matching no document: this method returns null without applying
    /// <paramref name="update"/> at all.
    /// </summary>
    public Task<AccreditationApplicationModel?> UpdateFieldsAsync(
        ObjectId id,
        FilterDefinition<AccreditationApplicationModel>? guardFilter,
        UpdateDefinition<AccreditationApplicationModel> update,
        CancellationToken cancellationToken = default
    )
    {
        var idx = _store.FindIndex(a => a.Id == id);
        if (idx < 0)
            return Task.FromResult<AccreditationApplicationModel?>(null);

        var serializer = BsonSerializer.LookupSerializer<AccreditationApplicationModel>();
        var renderArgs = new RenderArgs<AccreditationApplicationModel>(
            serializer,
            BsonSerializer.SerializerRegistry
        );

        var storedDoc = _store[idx].ToBsonDocument();

        if (guardFilter is not null)
        {
            var renderedGuard = guardFilter.Render(renderArgs).AsBsonDocument;
            if (!MatchesFilter(storedDoc, renderedGuard))
                return Task.FromResult<AccreditationApplicationModel?>(null);
        }

        // Mirrors AccreditationApplicationPersistence.UpdateFieldsAsync combining these into every
        // call - without them, this fake's Version never moves and every StatusChangedFromCase-
        // ManagementAsync-vs-Resubmit/Withdraw race test can't tell "webhook read-then-replaced
        // after this write" from "webhook read-then-replaced before it", since only Version (via
        // UpdateAsync's own RA-516 check) orders the two.
        var combinedUpdate = Builders<AccreditationApplicationModel>.Update.Combine(
            update,
            Builders<AccreditationApplicationModel>.Update.Set(a => a.UpdatedAt, DateTime.UtcNow),
            Builders<AccreditationApplicationModel>.Update.Inc(a => a.Version, 1L)
        );
        var rendered = combinedUpdate.Render(renderArgs).AsBsonDocument;

        if (rendered.TryGetValue("$set", out var setOps))
            foreach (var el in setOps.AsBsonDocument)
            {
                var parent = NavigateToParent(storedDoc, el.Name, out var leaf);
                parent[leaf] = el.Value;
            }

        if (rendered.TryGetValue("$inc", out var incOps))
            foreach (var el in incOps.AsBsonDocument)
            {
                var parent = NavigateToParent(storedDoc, el.Name, out var leaf);
                var current = parent.Contains(leaf) ? parent[leaf].ToInt64() : 0L;
                parent[leaf] = current + el.Value.ToInt64();
            }

        if (rendered.TryGetValue("$push", out var pushOps))
            foreach (var el in pushOps.AsBsonDocument)
            {
                var parent = NavigateToParent(storedDoc, el.Name, out var leaf);
                var array =
                    parent.Contains(leaf) && parent[leaf] is BsonArray existing
                        ? existing
                        : new BsonArray();
                if (el.Value is BsonDocument pushDoc && pushDoc.Contains("$each"))
                    array.AddRange(pushDoc["$each"].AsBsonArray);
                else
                    array.Add(el.Value);
                parent[leaf] = array;
            }

        var deserialized = BsonSerializer.Deserialize<AccreditationApplicationModel>(storedDoc);
        _store[idx] = deserialized;
        return Task.FromResult<AccreditationApplicationModel?>(deserialized);
    }

    // Evaluates a rendered guard FilterDefinition against a stored (BSON-serialized) document.
    // Deliberately scoped to just the operators Resubmit's/Withdraw's own guard filters ever
    // produce - $ne and $not+$size - the same "interpret only what production code actually
    // generates" scope NavigateToParent/the $set/$inc/$push handling above already keep to; an
    // unrecognised operator throws rather than silently matching, so a new guard shape added
    // later fails loudly here instead of passing tests it shouldn't.
    private static bool MatchesFilter(BsonDocument doc, BsonDocument filter)
    {
        foreach (var el in filter)
        {
            var actual = GetDottedValue(doc, el.Name);
            if (!MatchesCondition(actual, el.Value))
                return false;
        }
        return true;
    }

    private static bool MatchesCondition(BsonValue? actual, BsonValue expected)
    {
        if (expected is BsonDocument condDoc && condDoc.ElementCount > 0 && condDoc.Names.All(n => n.StartsWith('$')))
        {
            foreach (var condition in condDoc)
            {
                switch (condition.Name)
                {
                    case "$ne":
                        if (actual is not null && actual == condition.Value)
                            return false;
                        break;
                    case "$not":
                        if (MatchesCondition(actual, condition.Value))
                            return false;
                        break;
                    case "$size":
                        // $size never matches a missing/non-array field, even against 0 - a
                        // missing array has no size at all, it isn't "an array of size 0". Getting
                        // this wrong here inverted Not(Size(x, 0))'s result for a genuinely absent
                        // field (should match - nothing to guard against - but matched the $size
                        // condition instead, so Not() rejected it).
                        if (actual is not BsonArray sizedArray)
                            return false;
                        if (sizedArray.Count != condition.Value.ToInt32())
                            return false;
                        break;
                    default:
                        throw new NotSupportedException(
                            $"FakeAccreditationApplicationPersistence's guard-filter evaluator doesn't "
                                + $"understand '{condition.Name}' - only $ne/$not/$size are implemented, "
                                + "matching what Resubmit's/Withdraw's guard filters actually produce."
                        );
                }
            }
            return true;
        }

        return actual is not null && actual == expected;
    }

    // Walks a dotted path down a BsonDocument for reads (the sibling to NavigateToParent's writing
    // walk above), returning null for any missing intermediate - the same "missing field never
    // matches $size" semantics Mongo itself applies server-side.
    private static BsonValue? GetDottedValue(BsonDocument root, string dottedPath)
    {
        BsonValue current = root;
        foreach (var part in dottedPath.Split('.'))
        {
            if (current is BsonDocument doc && doc.TryGetValue(part, out var next))
                current = next;
            else
                return null;
        }
        return current;
    }

    // Walks a dotted Mongo field path (e.g. "query.queriedSectionKeys") down `root`, creating any
    // missing intermediate subdocuments along the way (mirroring how Mongo itself materialises a
    // dotted $set/$push path server-side), and returns the immediate parent document plus the
    // final path segment.
    private static BsonDocument NavigateToParent(
        BsonDocument root,
        string dottedPath,
        out string leafName
    )
    {
        var parts = dottedPath.Split('.');
        leafName = parts[^1];
        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current.TryGetValue(parts[i], out var child) && child is BsonDocument childDoc)
            {
                current = childDoc;
            }
            else
            {
                var newDoc = new BsonDocument();
                current[parts[i]] = newDoc;
                current = newDoc;
            }
        }
        return current;
    }

    private static AccreditationApplicationModel ShallowCopy(AccreditationApplicationModel src) =>
        new()
        {
            Id = src.Id,
            OrganisationId = src.OrganisationId,
            // RA-503: was missing entirely - GetByIdAsync silently dropped OrgId on every read
            // through this fake, latent until GetById's backfill-and-persist logic needed to see
            // a previously-stored value survive a round trip through this test double.
            OrgId = src.OrgId,
            Year = src.Year,
            RegistrationId = src.RegistrationId,
            IsExporter = src.IsExporter,
            MaterialType = src.MaterialType,
            // RA-448: another pre-existing gap - GlassRecyclingProcess was silently
            // dropped on every read through this fake, latent until a Glass-typed
            // application needed it back (RegulatoryNumberGenerator requires it).
            GlassRecyclingProcess = src.GlassRecyclingProcess,
            ApplicationStatus = src.ApplicationStatus,
            SourceReExAccreditationId = src.SourceReExAccreditationId,
            SourceYear = src.SourceYear,
            ApplicationReference = src.ApplicationReference,
            CaseManagementReference = src.CaseManagementReference,
            CaseManagementWorkItemId = src.CaseManagementWorkItemId,
            // RA-448: was missing entirely before this feature - GetByIdAsync/
            // GetByCaseManagementWorkItemIdAsync silently dropped RegistrationReference
            // on every read through this fake.
            RegistrationReference = src.RegistrationReference,
            // .ToList() genuinely copies the list - assigning the reference directly
            // would alias the stored record's own list, contradicting this method's
            // isolation contract (mutating a "copy" via .Add() would corrupt the
            // persisted record directly, and every GetByIdAsync caller would share
            // one non-thread-safe List instance).
            PreviousRegistrationNumbers = src.PreviousRegistrationNumbers.ToList(),
            AccreditationReference = src.AccreditationReference,
            PreviousAccreditationNumbers = src.PreviousAccreditationNumbers.ToList(),
            SubmittedBy = src.SubmittedBy,
            WithdrawalReason = src.WithdrawalReason,
            DateSent = src.DateSent,
            CaseManagementStatusUpdatedAt = src.CaseManagementStatusUpdatedAt,
            DateLastEdited = src.DateLastEdited,
            CreatedAt = src.CreatedAt,
            UpdatedAt = src.UpdatedAt,
            // RA-516: was missing, every read through this fake would silently reset the
            // concurrency token to 0, making every second update through it look like a version
            // conflict against the (already-incremented) stored record.
            Version = src.Version,
            Prns = src.Prns,
            BusinessPlan = src.BusinessPlan,
            SamplingPlan = src.SamplingPlan,
            // RA-482: was aliasing the stored record's own OverseasSites/Sites list, same
            // isolation gap PreviousRegistrationNumbers/PreviousAccreditationNumbers were already
            // fixed for above. Latent until UpdateIfOrsIdAbsentAsync started comparing the
            // "stored" copy against a site the endpoint had already Add()-ed to what turned out to
            // be the very same list instance -- a self-collision false positive on every write.
            OverseasSites = src.OverseasSites is null
                ? null
                : new AccreditationApplicationOverseasSites
                {
                    Sites = src.OverseasSites.Sites.ToList(),
                    SectionStatus = src.OverseasSites.SectionStatus,
                    Versions = src.OverseasSites.Versions.ToList(),
                },
            BesEvidence = src.BesEvidence,
            Query = src.Query,
        };
}
