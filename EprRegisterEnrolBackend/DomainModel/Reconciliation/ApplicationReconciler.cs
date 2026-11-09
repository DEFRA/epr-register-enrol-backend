using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using EprRegisterEnrolBackend.DomainModel.Seeding;
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
    ApplicationReadModel readModel,
    MigrationConflictPersistence conflicts
)
{
    private const string LegacyCollection = "accreditationApplications";
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

    internal int BatchSize { get; init; } = 100;

    /// <summary>Between batches, so a report doesn't hammer a shared test database.</summary>
    internal TimeSpan BatchPause { get; init; } = TimeSpan.FromMilliseconds(200);

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
            await Task.Delay(BatchPause, cancellationToken);
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
            var comparison = await session.WithTransactionAsync(
                (s, token) => CompareInSnapshotAsync(id, s, token),
                new TransactionOptions(readConcern: ReadConcern.Snapshot),
                cancellationToken
            );
            if (comparison is null)
            {
                tally.NotMigrated++;
                return;
            }

            tally.Record(id, comparison.Differences, comparison.ListedAsConflict);
        }
        // One document that cannot be read or projected is a finding for the report, not a reason to lose the report.
        catch (Exception ex)
            when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested
            )
        {
            tally.RecordUnreadable(id, ex);
        }
    }

    private sealed record Comparison(DocumentDifferences Differences, bool ListedAsConflict);

    // Null when the application has not been migrated. Conflicts are only looked up for an application that differs.
    private async Task<Comparison?> CompareInSnapshotAsync(
        ObjectId id,
        IClientSessionHandle session,
        CancellationToken cancellationToken
    )
    {
        var legacy = await applications.GetByObjectIdAsync(id, session, cancellationToken);
        var sources = await readModel.LoadAsync(id, session, cancellationToken);
        if (legacy is null || sources is null)
        {
            return null;
        }

        var differences = Differences(legacy, sources);
        var listed =
            differences.Paths.Count > 0
            && await conflicts.IsListedAsync(id, session, cancellationToken);
        return new Comparison(differences, listed);
    }

    private static DocumentDifferences Differences(
        AccreditationApplicationModel legacy,
        ApplicationSources sources
    )
    {
        NormaliseSites(legacy, sources);
        var differences = BsonDifferences.Compare(
            legacy.ToBsonDocument(),
            ApplicationProjection.ToLegacy(sources).ToBsonDocument()
        );
        differences.Paths.ExceptWith(s_ignored);
        differences.ModelOnly.IntersectWith(differences.Paths);
        return differences;
    }

    // What the migration does on purpose: promote a pre-RA-603 singular interim site, renumber once (Q30b), and turn
    // ReEx's raw date string into a date, so the same instant written differently is not a difference.
    private static void NormaliseSites(
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
            if (SeedEntityMapper.ParseApprovalDate(site.ValidFrom) is { } approved)
            {
                site.ValidFrom = ApplicationProjection.FormatApprovalDate(approved);
            }

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
        private readonly Dictionary<string, long> _modelOnly = [];
        private readonly List<ObjectId> _sample = [];
        private long _matching;
        private long _withDifferences;
        private long _acceptedConflicts;
        private long _unreadable;
        private readonly Dictionary<string, long> _unreadableKinds = [];

        public long Applications { get; set; }
        public long NotMigrated { get; set; }

        public void Record(ObjectId id, DocumentDifferences differences, bool listedAsConflict)
        {
            var paths = differences.Paths;
            var modelOnly = differences.ModelOnly;
            if (paths.Count == 0)
            {
                _matching++;
                return;
            }

            // A value only the model holds is never accepted: nothing on the legacy side explains it.
            if (listedAsConflict && modelOnly.Count == 0)
            {
                _acceptedConflicts++;
                return;
            }

            foreach (var path in paths)
            {
                var counts = modelOnly.Contains(path) ? _modelOnly : _differences;
                counts[path] = counts.GetValueOrDefault(path) + 1;
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
                _acceptedConflicts,
                NotMigrated,
                _unreadable,
                _unreadableKinds,
                _differences,
                _modelOnly,
                _sample
            );
    }
}
