using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>
/// Phase 4 (design doc §6.1): walks the legacy applications at startup and migrates those with no current version in
/// <c>applicationVersions</c>. Each registration group is its own transaction, so a run stopped by a timeout or a
/// restart keeps what it finished and the next one has less to do. Anything dual-write has already migrated is
/// skipped without opening a transaction.
/// </summary>
/// <remarks>
/// A <see cref="BackgroundService"/>, because CDP kills a container that takes too long to start. A group that fails
/// is recorded and logged, shows as Degraded, and is tried again at the next startup; the run carries on without it.
/// Logs and failure records hold ids and error kinds only, never personal data.
/// </remarks>
public sealed class DomainModelMigrationService(
    IServiceProvider services,
    IDomainModelSchemaStatus schema,
    IDomainModelMigrationStatus status,
    IMongoDbClientFactory clientFactory,
    TimeProvider clock,
    ILogger<DomainModelMigrationService> logger
) : BackgroundService
{
    private const string LegacyCollection = "accreditationApplications";

    private readonly IDomainModelMigrationStatus _status = status;
    private readonly IMongoDbClientFactory _clientFactory = clientFactory;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<DomainModelMigrationService> _logger = logger;
    private readonly string _owner = Guid.NewGuid().ToString("N");

    internal int BatchSize { get; init; } = 100;
    internal TimeSpan BatchPause { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Longer than one group can take: a 60-second transaction, retried up to three times.</summary>
    internal TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often to look again for the schema, or for a lease another instance holds.</summary>
    internal TimeSpan WaitDelay { get; init; } = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is never held up by anything below.
        await Task.Yield();
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down; the next startup carries on.
        }
        catch (Exception ex)
        {
            var kind = MongoErrors.Kind(ex);
            _status.RecordStopped(kind);
            _logger.LogError("DomainModelMigration stopped: step run, error {ErrorKind}", kind);
        }
    }

    // Extracted so tests can await it deterministically.
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        // The unique indexes must exist before anything relies on them to settle a race with a live write.
        while (schema.Result.Status == HealthStatus.Unhealthy)
        {
            await Task.Delay(WaitDelay, _clock, cancellationToken);
        }

        // Resolved only now: constructing these builds indexes, which must not hold up startup.
        var migrator = services.GetRequiredService<IApplicationGroupMigrator>();
        var persistence = services.GetRequiredService<DomainModelMigrationPersistence>();

        var state = await AcquireLeaseAsync(persistence, cancellationToken);
        _status.RecordFailedGroups(state.Failures.Count);
        try
        {
            await new Pass(this, migrator, persistence, state).ExecuteAsync(cancellationToken);
        }
        finally
        {
            // Not the host's token: on shutdown the next instance should not have to wait for the lease to expire.
            await persistence.ReleaseLeaseAsync(_owner, CancellationToken.None);
        }
    }

    private async Task<DomainModelMigrationState> AcquireLeaseAsync(
        DomainModelMigrationPersistence persistence,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var state = await persistence.TryAcquireLeaseAsync(
                _owner,
                now,
                now + LeaseDuration,
                cancellationToken
            );
            if (state is not null)
            {
                return state;
            }

            await Task.Delay(WaitDelay, _clock, cancellationToken);
        }
    }

    /// <summary>One legacy application, read as ids only: an old document may not deserialise.</summary>
    private sealed record Candidate(ObjectId Id, string Group);

    /// <summary>One walk over the legacy applications. Never shared between runs.</summary>
    private sealed class Pass(
        DomainModelMigrationService service,
        IApplicationGroupMigrator migrator,
        DomainModelMigrationPersistence persistence,
        DomainModelMigrationState state
    )
    {
        private static readonly string s_organisationElement = LegacyElement(
            nameof(AccreditationApplicationModel.OrganisationId)
        );
        private static readonly string s_registrationElement = LegacyElement(
            nameof(AccreditationApplicationModel.RegistrationId)
        );
        private static readonly string s_entityIdElement = BsonClassMap
            .LookupClassMap(typeof(VersionedEntityBase))
            .GetMemberMap(nameof(VersionedEntityBase.EntityId))
            .ElementName;

        private readonly Dictionary<string, DomainModelMigrationFailure> _earlierFailures =
            state.Failures.ToDictionary(f => f.Group);
        private readonly Dictionary<string, DomainModelMigrationFailure> _failures = [];
        private readonly HashSet<string> _settledGroups = [];
        private long _examined;
        private long _alreadyMigrated;
        private long _migrated;

        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            ObjectId? after = null;
            while (true)
            {
                var batch = await NextBatchAsync(after, cancellationToken);
                if (!await WorkThroughAsync(batch, cancellationToken))
                {
                    return; // Another instance has taken the lease, so it carries on.
                }

                if (batch.Count < service.BatchSize)
                {
                    break;
                }

                after = batch[^1].Id;
                await Task.Delay(service.BatchPause, service._clock, cancellationToken);
            }

            await FinishAsync(cancellationToken);
        }

        private async Task<List<Candidate>> NextBatchAsync(
            ObjectId? after,
            CancellationToken cancellationToken
        )
        {
            var filter = after is { } id
                ? Builders<BsonDocument>.Filter.Gt("_id", id)
                : Builders<BsonDocument>.Filter.Empty;
            var documents = await service
                ._clientFactory.GetCollection<BsonDocument>(LegacyCollection)
                .Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .Limit(service.BatchSize)
                .Project(
                    Builders<BsonDocument>
                        .Projection.Include(s_organisationElement)
                        .Include(s_registrationElement)
                )
                .ToListAsync(cancellationToken);
            return documents.ConvertAll(ToCandidate);
        }

        private static Candidate ToCandidate(BsonDocument document)
        {
            var id = document["_id"].AsObjectId;
            var registration = document.GetValue(s_registrationElement, BsonNull.Value);
            return new Candidate(
                id,
                registration.IsBsonNull
                    ? id.ToString()
                    : $"{document.GetValue(s_organisationElement, BsonNull.Value)}|{registration}"
            );
        }

        // False when this instance has lost the lease.
        private async Task<bool> WorkThroughAsync(
            List<Candidate> batch,
            CancellationToken cancellationToken
        )
        {
            var migrated = await AlreadyMigratedAsync(batch, cancellationToken);
            foreach (var candidate in batch)
            {
                _examined++;
                if (migrated.Contains(candidate.Id))
                {
                    _alreadyMigrated++;
                }
                else if (
                    !_settledGroups.Contains(candidate.Group)
                    && !await MigrateGroupAsync(candidate, cancellationToken)
                )
                {
                    return false;
                }
            }

            return await SaveAsync(cancellationToken);
        }

        // Ids only, and from raw documents, so a version in an older shape still counts as migrated here.
        private async Task<HashSet<ObjectId>> AlreadyMigratedAsync(
            List<Candidate> batch,
            CancellationToken cancellationToken
        )
        {
            var filter = Builders<ApplicationEntity>.Filter;
            var documents = await service
                ._clientFactory.GetCollection<ApplicationEntity>(
                    DomainModelCollections.ApplicationVersions
                )
                .Find(
                    filter.In(a => a.EntityId, batch.Select(c => c.Id))
                        & filter.Eq(a => a.IsCurrent, true)
                )
                .Project<BsonDocument>(
                    Builders<ApplicationEntity>.Projection.Include(a => a.EntityId)
                )
                .ToListAsync(cancellationToken);
            return documents.Select(d => d[s_entityIdElement].AsObjectId).ToHashSet();
        }

        // Its own transaction, so anything that goes wrong here costs this group alone.
        private async Task<bool> MigrateGroupAsync(
            Candidate candidate,
            CancellationToken cancellationToken
        )
        {
            _settledGroups.Add(candidate.Group);
            try
            {
                var result = await InsertRaceRetry.RunInTransactionAsync(
                    service._clientFactory,
                    (session, token) => migrator.EnsureMigratedAsync(candidate.Id, session, token),
                    attempt =>
                        service._logger.LogWarning(
                            "DomainModelMigration lost an insert race and will retry: application {ApplicationId}, attempt {Attempt}",
                            candidate.Id,
                            attempt
                        ),
                    cancellationToken
                );
                _migrated += result.MigratedApplications;
                service._logger.LogRecorded(result.Conflicts);
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException
                    || !cancellationToken.IsCancellationRequested
                )
            {
                RecordFailure(candidate, MongoErrors.Kind(ex));
            }

            return await SaveAsync(cancellationToken);
        }

        private void RecordFailure(Candidate candidate, string errorKind)
        {
            var attempts =
                (
                    _earlierFailures.TryGetValue(candidate.Group, out var earlier)
                        ? earlier.Attempts
                        : 0
                ) + 1;
            _failures[candidate.Group] = new DomainModelMigrationFailure
            {
                Group = candidate.Group,
                ApplicationId = candidate.Id,
                Attempts = attempts,
                LastErrorKind = errorKind,
                LastFailedAt = service._clock.GetUtcNow().UtcDateTime,
            };
            service._status.RecordFailedGroups(CurrentFailures().Count);
            service._logger.LogError(
                "DomainModelMigration failed: step ensure-migrated, application {ApplicationId}, attempts {Attempts}, error {ErrorKind}",
                candidate.Id,
                attempts,
                errorKind
            );
        }

        // Until the walk reaches them, earlier failures still stand.
        private List<DomainModelMigrationFailure> CurrentFailures() =>
            _earlierFailures
                .Values.Where(f => !_failures.ContainsKey(f.Group))
                .Concat(_failures.Values)
                .ToList();

        private Task<bool> SaveAsync(CancellationToken cancellationToken) =>
            persistence.SaveProgressAsync(
                service._owner,
                service._clock.GetUtcNow().UtcDateTime + service.LeaseDuration,
                Progress(CurrentFailures(), completedAt: null),
                cancellationToken
            );

        private async Task FinishAsync(CancellationToken cancellationToken)
        {
            var now = service._clock.GetUtcNow().UtcDateTime;
            var failures = _failures.Values.ToList();
            await persistence.SaveProgressAsync(
                service._owner,
                now + service.LeaseDuration,
                Progress(failures, completedAt: now),
                cancellationToken
            );
            service._status.RecordFailedGroups(failures.Count);
            service._logger.LogInformation(
                "DomainModelMigration pass complete: examined {Examined}, already migrated {AlreadyMigrated}, migrated {Migrated}, failed groups {FailedGroups}",
                _examined,
                _alreadyMigrated,
                _migrated,
                failures.Count
            );
        }

        private DomainModelMigrationState Progress(
            List<DomainModelMigrationFailure> failures,
            DateTime? completedAt
        ) =>
            new()
            {
                PassCompletedAt = completedAt,
                Examined = _examined,
                AlreadyMigrated = _alreadyMigrated,
                Migrated = _migrated,
                Failures = failures,
            };

        private static string LegacyElement(string member) =>
            BsonClassMap
                .LookupClassMap(typeof(AccreditationApplicationModel))
                .GetMemberMap(member)
                .ElementName;
    }
}
