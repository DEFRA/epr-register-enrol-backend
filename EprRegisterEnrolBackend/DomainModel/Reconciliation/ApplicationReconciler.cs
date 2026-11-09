using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Reconciliation;

/// <summary>
/// Projects each migrated application through <see cref="ApplicationReadModel"/> and compares it with its legacy
/// document (design doc B10). Each comparison reads both sides from one snapshot, so a live write cannot show up as a
/// difference. It only reads.
/// </summary>
public sealed class ApplicationReconciler(
    IMongoDbClientFactory clientFactory,
    IAccreditationApplicationPersistence applications,
    ApplicationReadModel readModel
)
{
    private const string LegacyCollection = "accreditationApplications";
    private const int BatchSize = 100;
    private const int MaxSampleIds = 20;

    /// <summary>Bookkeeping that belongs to each store, and the model's own foreign keys on the legacy document.</summary>
    private static readonly HashSet<string> s_ignored =
    [
        "_id",
        "version",
        "createdAt",
        "updatedAt",
        "caseManagementReference",
        "registrationEntityId",
        "accreditationEntityId",
        "overseasSites.sites[].overseasSiteEntityId",
        "overseasSites.sites[].previousSites[].overseasSiteEntityId",
    ];

    /// <summary>
    /// Legacy fields with no home in the model yet, because they wait on ReEx enrichment (Q35) or were never mapped.
    /// Counted apart from differences, so they stay visible without hiding a real one.
    /// </summary>
    private static readonly HashSet<string> s_notInModel =
    [
        "siteAddress",
        "companyRegisterAddressPostcode",
        "companyRegisteredAddress",
        "isUkRegisteredAddress",
        "permitNumbers",
        "sourceReExAccreditationId",
        "sourceYear",
        "previousAccreditationNumbers",
        "submitterContactDetails",
        "overseasSites.sites[].validFrom",
    ];

    public async Task<ReconciliationReport> RunAsync(CancellationToken cancellationToken)
    {
        var tally = new Tally();
        ObjectId? after = null;
        while (true)
        {
            var ids = await NextBatchAsync(after, cancellationToken);
            foreach (var id in ids)
            {
                await CompareAsync(id, tally, cancellationToken);
            }

            if (ids.Count < BatchSize)
            {
                return tally.ToReport();
            }

            after = ids[^1];
        }
    }

    private async Task<List<ObjectId>> NextBatchAsync(
        ObjectId? after,
        CancellationToken cancellationToken
    )
    {
        var filter = after is { } id
            ? Builders<BsonDocument>.Filter.Gt("_id", id)
            : Builders<BsonDocument>.Filter.Empty;
        var documents = await clientFactory
            .GetCollection<BsonDocument>(LegacyCollection)
            .Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .Limit(BatchSize)
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .ToListAsync(cancellationToken);
        return documents.ConvertAll(d => d["_id"].AsObjectId);
    }

    private async Task CompareAsync(ObjectId id, Tally tally, CancellationToken cancellationToken)
    {
        tally.Applications++;
        try
        {
            using var session = await clientFactory
                .GetClient()
                .StartSessionAsync(cancellationToken: cancellationToken);
            var (legacy, sources) = await session.WithTransactionAsync(
                async (s, token) =>
                    (
                        await applications.GetByObjectIdAsync(id, s, token),
                        await readModel.LoadAsync(id, s, token)
                    ),
                new TransactionOptions(readConcern: ReadConcern.Snapshot),
                cancellationToken
            );
            if (legacy is null || sources is null)
            {
                tally.NotMigrated++;
                return;
            }

            tally.Record(id, Differences(legacy, sources));
        }
        // A document the current classes cannot read or project: a finding for the report, not a reason to stop.
        catch (Exception ex) when (ex is FormatException or MongoDB.Bson.BsonException)
        {
            tally.RecordUnreadable(id, ex);
        }
    }

    private static HashSet<string> Differences(
        AccreditationApplicationModel legacy,
        ApplicationSources sources
    )
    {
        NormaliseInterimSites(legacy, sources);
        return BsonDifferences.Paths(
            legacy.ToBsonDocument(),
            ApplicationProjection.ToLegacy(sources).ToBsonDocument()
        );
    }

    // Two things the migration does on purpose: promote a pre-RA-603 singular interim site, and renumber once (Q30b).
    private static void NormaliseInterimSites(
        AccreditationApplicationModel legacy,
        ApplicationSources sources
    )
    {
        var renumbered = sources
            .OverseasSites.Values.SelectMany(site => site.InterimSites)
            .SelectMany(interim =>
                interim.PreviousSiteNumbers.Select(old => (Old: old, New: interim.SiteNumber))
            )
            .GroupBy(pair => pair.Old)
            .ToDictionary(group => group.Key, group => group.First().New);

        foreach (var site in legacy.OverseasSites?.Sites ?? [])
        {
            InterimSiteSync.Normalise(site);
            foreach (
                var interim in site.InterimSites.Append(site.InterimSite).OfType<InterimSiteModel>()
            )
            {
                interim.SiteNumber = renumbered.GetValueOrDefault(
                    interim.SiteNumber,
                    interim.SiteNumber
                );
            }
        }
    }

    private sealed class Tally
    {
        private readonly Dictionary<string, long> _differences = [];
        private readonly Dictionary<string, long> _notInModel = [];
        private readonly List<ObjectId> _sample = [];
        private long _matching;
        private long _withDifferences;
        private long _unreadable;
        private readonly Dictionary<string, long> _unreadableKinds = [];

        public long Applications { get; set; }
        public long NotMigrated { get; set; }

        public void Record(ObjectId id, HashSet<string> paths)
        {
            paths.ExceptWith(s_ignored);
            foreach (var path in paths)
            {
                var counts = s_notInModel.Contains(path) ? _notInModel : _differences;
                counts[path] = counts.GetValueOrDefault(path) + 1;
            }

            if (paths.All(s_notInModel.Contains))
            {
                _matching++;
                return;
            }

            _withDifferences++;
            Sample(id);
        }

        public void RecordUnreadable(ObjectId id, Exception ex)
        {
            _unreadable++;
            var kind = MongoErrors.Kind(ex);
            _unreadableKinds[kind] = _unreadableKinds.GetValueOrDefault(kind) + 1;
            Sample(id);
        }

        private void Sample(ObjectId id)
        {
            if (_sample.Count < MaxSampleIds)
            {
                _sample.Add(id);
            }
        }

        public ReconciliationReport ToReport() =>
            new(
                Applications,
                _matching,
                _withDifferences,
                NotMigrated,
                _unreadable,
                _unreadableKinds,
                _differences,
                _notInModel,
                _sample
            );
    }
}
