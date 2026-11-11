using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Migration;

/// <summary>
/// Phase 4 (design doc §6.1) on a real replica set: what is migrated, what is skipped, and how a run that fails or is
/// stopped part way picks up.
/// </summary>
public sealed class DomainModelMigrationServiceTests : IDisposable
{
    private const string ReExOrganisationId = "6512a0f0c0ffee0000000001";
    private static readonly DateTime Now = new(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly TestClock _clock = new(Now);
    private readonly AccreditationApplicationPersistence _applications;
    private readonly DomainModelMigrationPersistence _persistence;
    private readonly ApplicationGroupMigrator _realMigrator;
    private readonly SpyMigrator _migrator;
    private readonly DomainModelSchemaStatus _schema = new();
    private readonly DomainModelMigrationStatus _status = new();
    private readonly CapturingLogger<DomainModelMigrationService> _logger = new();

    public DomainModelMigrationServiceTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("modelmigration");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        var logs = NullLoggerFactory.Instance;
        _applications = new AccreditationApplicationPersistence(_factory, logs);
        _persistence = new DomainModelMigrationPersistence(_factory, logs);
        _realMigrator = new ApplicationGroupMigrator(
            _applications,
            new ApplicationGroupStores(
                new OrganisationPersistence(_factory, logs),
                new RegistrationPersistence(_factory, logs, _clock),
                new OverseasSitePersistence(_factory, logs, _clock),
                new ApplicationVersionPersistence(_factory, logs, _clock),
                new AccreditationPersistence(_factory, logs, _clock),
                new MigrationConflictPersistence(_factory, logs)
            ),
            _clock
        );
        _migrator = new SpyMigrator(_realMigrator);
        _schema.Record(HealthCheckResult.Healthy());
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    /// <summary>Signals the moment the service starts to wait, so a test need not guess how long that takes.</summary>
    private sealed class WaitSignallingClock(TimeProvider inner) : TimeProvider
    {
        private readonly TaskCompletionSource _waiting = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task Waiting => _waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            _waiting.TrySetResult();
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    /// <summary>Counts the groups attempted, and lets a test fail or interrupt one.</summary>
    private sealed class SpyMigrator(IApplicationGroupMigrator inner) : IApplicationGroupMigrator
    {
        public List<ObjectId> Calls { get; } = [];
        public Func<ObjectId, Task>? Before { get; set; }
        public Action<ObjectId>? After { get; set; }

        public async Task<GroupMigrationResult> EnsureMigratedAsync(
            ObjectId applicationId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default
        )
        {
            lock (Calls)
            {
                Calls.Add(applicationId);
            }

            if (Before is not null)
            {
                await Before(applicationId);
            }

            var result = await inner.EnsureMigratedAsync(applicationId, session, cancellationToken);
            After?.Invoke(applicationId);
            return result;
        }
    }

    private DomainModelMigrationService Service(
        int batchSize = 100,
        TimeProvider? clock = null,
        TimeSpan? recentPassWindow = null
    )
    {
        var services = new ServiceCollection()
            .AddSingleton<IApplicationGroupMigrator>(_migrator)
            .AddSingleton(_persistence)
            .BuildServiceProvider();
        return new DomainModelMigrationService(
            services,
            _schema,
            _status,
            _factory,
            clock ?? _clock,
            _logger
        )
        {
            BatchSize = batchSize,
            BatchPause = TimeSpan.Zero,
            RecentPassWindow = recentPassWindow ?? TimeSpan.Zero,
            WaitDelay = TimeSpan.FromMilliseconds(20),
        };
    }

    private async Task<ObjectId> ApplicationAsync(string? registrationId, int year = 2027)
    {
        var application = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = ReExOrganisationId,
            OrganisationName = "Acme Recycling Ltd",
            RegistrationId = registrationId,
            IsExporter = true,
            Nation = Nation.England,
            MaterialType = MaterialType.Plastic,
            Year = year,
            ApplicationStatus = ApplicationStatus.Started,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        return (await _applications.CreateAsync(application))!.Id!.Value;
    }

    private static string Registration(int number) => $"6512a0f0c0ffee00000001{number:D2}";

    private Task<GroupMigrationResult> LazyMigrateAsync(ObjectId applicationId) =>
        InsertRaceRetry.RunInTransactionAsync(
            _factory,
            (session, token) => _migrator.EnsureMigratedAsync(applicationId, session, token),
            _ => { },
            Ct
        );

    private async Task<HashSet<ObjectId>> MigratedAsync() =>
        (
            await _factory
                .GetCollection<ApplicationEntity>(DomainModelCollections.ApplicationVersions)
                .Find(Builders<ApplicationEntity>.Filter.Eq(a => a.IsCurrent, true))
                .ToListAsync(Ct)
        )
            .Select(a => a.EntityId)
            .ToHashSet();

    private Task<long> VersionCountAsync() =>
        _factory
            .GetCollection<ApplicationEntity>(DomainModelCollections.ApplicationVersions)
            .CountDocumentsAsync(FilterDefinition<ApplicationEntity>.Empty, cancellationToken: Ct);

    private Task<DomainModelMigrationState> StateAsync() =>
        _factory
            .GetCollection<DomainModelMigrationState>(DomainModelCollections.DomainModelMigrations)
            .Find(FilterDefinition<DomainModelMigrationState>.Empty)
            .SingleAsync(Ct);

    // The startup run fills the number on an Organisation a seed wrote without one, from the legacy application.
    [Fact]
    public async Task Run_SeededOrganisationWithoutANumber_IsFilledFromTheLegacyApplication()
    {
        await _factory
            .GetCollection<OrganisationEntity>(DomainModelCollections.Organisations)
            .InsertOneAsync(
                new OrganisationEntity
                {
                    Id = ObjectId.GenerateNewId(),
                    ReExOrganisationId = ReExOrganisationId,
                },
                cancellationToken: Ct
            );
        var id = await ApplicationAsync(Registration(1));
        await _factory
            .GetCollection<BsonDocument>(DomainModelCollections.LegacyApplications)
            .UpdateOneAsync(
                new BsonDocument("_id", id),
                new BsonDocument("$set", new BsonDocument("orgId", 520458)),
                cancellationToken: Ct
            );

        await Service().RunAsync(Ct);

        (await MigratedAsync()).Should().Contain(id);
        (
            await _factory
                .GetCollection<OrganisationEntity>(DomainModelCollections.Organisations)
                .Find(FilterDefinition<OrganisationEntity>.Empty)
                .SingleAsync(Ct)
        )
            .OrganisationNumber.Should()
            .Be(520458);
    }

    [Fact]
    public async Task Run_MigratesEveryApplication_AcrossBatches()
    {
        var ids = new List<ObjectId>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await ApplicationAsync(Registration(i)));
        }

        await Service(batchSize: 2).RunAsync(Ct);

        (await MigratedAsync()).Should().BeEquivalentTo(ids);
        var state = await StateAsync();
        state.Examined.Should().Be(5);
        state.Migrated.Should().Be(5);
        state.AlreadyMigrated.Should().Be(0);
        state.PassCompletedAt.Should().Be(Now);
        state.LeaseOwner.Should().BeNull();
        _status.Result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Run_ApplicationWithNoRegistrationYet_IsItsOwnGroup()
    {
        var first = await ApplicationAsync(registrationId: null);
        var second = await ApplicationAsync(registrationId: null);

        await Service().RunAsync(Ct);

        (await MigratedAsync()).Should().BeEquivalentTo([first, second]);
        _migrator.Calls.Should().Equal(first, second);
    }

    [Fact]
    public async Task Run_WhatDualWriteAlreadyMigrated_IsSkippedWithoutATransaction()
    {
        var lazy = await ApplicationAsync(Registration(1));
        var untouched = await ApplicationAsync(Registration(2));
        await LazyMigrateAsync(lazy);
        var versionsBefore = await VersionCountAsync();
        _migrator.Calls.Clear();

        await Service().RunAsync(Ct);

        _migrator.Calls.Should().Equal(untouched);
        (await VersionCountAsync()).Should().Be(versionsBefore + 1);
        var state = await StateAsync();
        state.AlreadyMigrated.Should().Be(1);
        state.Migrated.Should().Be(1);
    }

    [Fact]
    public async Task Run_SeededStampWithNoApplicationVersion_IsStillMigrated()
    {
        // A seed stamps registrationEntityId but writes no application version, so the stamp proves nothing.
        var seeded = await ApplicationAsync(Registration(1));
        await _factory
            .GetCollection<BsonDocument>("accreditationApplications")
            .UpdateOneAsync(
                new BsonDocument("_id", seeded),
                new BsonDocument(
                    "$set",
                    new BsonDocument("registrationEntityId", ObjectId.GenerateNewId())
                ),
                cancellationToken: Ct
            );

        await Service().RunAsync(Ct);

        (await MigratedAsync()).Should().Contain(seeded);
    }

    [Fact]
    public async Task Run_AGroupOfSeveralApplications_IsOneAttemptAndOneTransaction()
    {
        var first = await ApplicationAsync(Registration(1), 2027);
        var second = await ApplicationAsync(Registration(1), 2028);
        var third = await ApplicationAsync(Registration(1), 2029);

        await Service().RunAsync(Ct);

        _migrator.Calls.Should().Equal(first);
        (await MigratedAsync()).Should().BeEquivalentTo([first, second, third]);
        (await StateAsync()).Migrated.Should().Be(3);
    }

    [Fact]
    public async Task Run_SecondRun_OnlyReads()
    {
        await ApplicationAsync(Registration(1));
        await Service().RunAsync(Ct);
        _migrator.Calls.Clear();
        var versions = await VersionCountAsync();

        await Service().RunAsync(Ct);

        _migrator.Calls.Should().BeEmpty();
        (await VersionCountAsync()).Should().Be(versions);
        var state = await StateAsync();
        state.AlreadyMigrated.Should().Be(1);
        state.Migrated.Should().Be(0);
    }

    [Fact]
    public async Task Run_StoppedPartWay_KeepsFinishedGroupsAndTheNextRunDoesTheRest()
    {
        var finished = await ApplicationAsync(Registration(1));
        var interrupted = await ApplicationAsync(Registration(2));
        var notReached = await ApplicationAsync(Registration(3));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _migrator.Before = async id =>
        {
            if (id == interrupted)
            {
                await shutdown.CancelAsync();
                shutdown.Token.ThrowIfCancellationRequested();
            }
        };

        var stopped = () => Service().RunAsync(shutdown.Token);

        await stopped.Should().ThrowAsync<OperationCanceledException>();
        (await MigratedAsync()).Should().BeEquivalentTo([finished]);
        (await StateAsync()).LeaseOwner.Should().BeNull("a shutdown hands the lease straight back");

        _migrator.Before = null;
        _migrator.Calls.Clear();
        await Service().RunAsync(Ct);

        _migrator.Calls.Should().Equal(interrupted, notReached);
        (await MigratedAsync()).Should().BeEquivalentTo([finished, interrupted, notReached]);
    }

    [Fact]
    public async Task Run_AGroupThatFails_RollsBackAloneAndTheRunCarriesOn()
    {
        var before = await ApplicationAsync(Registration(1));
        var failing = await ApplicationAsync(Registration(2));
        var after = await ApplicationAsync(Registration(3));
        _migrator.After = id =>
        {
            if (id == failing)
            {
                throw new InvalidOperationException("contains someone's name");
            }
        };

        await Service().RunAsync(Ct);

        (await MigratedAsync()).Should().BeEquivalentTo([before, after]);
        var failure = (await StateAsync()).Failures.Should().ContainSingle().Subject;
        failure.ApplicationId.Should().Be(failing);
        failure.Attempts.Should().Be(1);
        failure.LastErrorKind.Should().Be(nameof(InvalidOperationException));
        _status.Result.Status.Should().Be(HealthStatus.Degraded);
        var line = _logger
            .Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error)
            .Subject;
        line.Message.Should().Contain(failing.ToString()).And.NotContain("someone's name");
        // The throw is in a lambda, so the nearest named method is the migrator that ran it.
        line.Message.Should()
            .Contain($"{nameof(InvalidOperationException)} at SpyMigrator.EnsureMigratedAsync");
        line.Exception.Should().BeNull();
    }

    [Fact]
    public async Task Run_AFailedGroup_IsTriedOnceAPassEvenWithSeveralApplications()
    {
        var first = await ApplicationAsync(Registration(1), 2027);
        await ApplicationAsync(Registration(1), 2028);
        _migrator.Before = _ => throw new InvalidOperationException();

        await Service().RunAsync(Ct);

        _migrator.Calls.Should().Equal(first);
        (await StateAsync()).Failures.Should().ContainSingle();
    }

    [Fact]
    public async Task Run_AFailedGroup_CountsAttemptsThenClearsWhenItMigrates()
    {
        var failing = await ApplicationAsync(Registration(1));
        _migrator.Before = _ => throw new InvalidOperationException();
        await Service().RunAsync(Ct);
        await Service().RunAsync(Ct);
        (await StateAsync()).Failures.Single().Attempts.Should().Be(2);

        _migrator.Before = null;
        await Service().RunAsync(Ct);

        (await MigratedAsync()).Should().Contain(failing);
        (await StateAsync()).Failures.Should().BeEmpty();
        _status.Result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Run_ALegacyDocumentThatCannotBeRead_IsAFailedGroupNotAStoppedRun()
    {
        var unreadable = ObjectId.GenerateNewId();
        await _factory
            .GetCollection<BsonDocument>("accreditationApplications")
            .InsertOneAsync(
                new BsonDocument
                {
                    { "_id", unreadable },
                    { "organisationId", ReExOrganisationId },
                    { "registrationId", Registration(1) },
                    { "applicationStatus", new BsonDocument("not", "a status") },
                },
                cancellationToken: Ct
            );
        var fine = await ApplicationAsync(Registration(2));

        await Service().RunAsync(Ct);

        (await MigratedAsync()).Should().BeEquivalentTo([fine]);
        (await StateAsync()).Failures.Single().ApplicationId.Should().Be(unreadable);
    }

    [Fact]
    public async Task Run_LeaseHeldByAnotherInstance_WaitsUntilItIsReleased()
    {
        var id = await ApplicationAsync(Registration(1));
        await _persistence.TryAcquireLeaseAsync("other", Now, Now.AddMinutes(5), Ct);

        var clock = new WaitSignallingClock(_clock);
        var running = Service(clock: clock).RunAsync(Ct);
        await clock.Waiting;

        _migrator.Calls.Should().BeEmpty();
        await _persistence.ReleaseLeaseAsync("other", Ct);
        await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        (await MigratedAsync()).Should().Contain(id);
    }

    [Fact]
    public async Task Run_LeaseLeftByADeadInstance_IsTakenOverOnceExpired()
    {
        var id = await ApplicationAsync(Registration(1));
        await _persistence.TryAcquireLeaseAsync(
            "dead",
            Now.AddMinutes(-10),
            Now.AddMinutes(-5),
            Ct
        );

        await Service().RunAsync(Ct);

        (await MigratedAsync()).Should().Contain(id);
    }

    [Fact]
    public async Task Run_LeaseLostPartWay_StopsWalking()
    {
        var first = await ApplicationAsync(Registration(1));
        await ApplicationAsync(Registration(2));
        _migrator.Before = async id =>
        {
            if (id == first)
            {
                // The lease expired while this instance stalled, and another instance has it now.
                await _factory
                    .GetCollection<DomainModelMigrationState>(
                        DomainModelCollections.DomainModelMigrations
                    )
                    .UpdateOneAsync(
                        FilterDefinition<DomainModelMigrationState>.Empty,
                        Builders<DomainModelMigrationState>.Update.Set(s => s.LeaseOwner, "newer"),
                        cancellationToken: Ct
                    );
            }
        };

        await Service().RunAsync(Ct);

        _migrator.Calls.Should().Equal(first);
        var state = await StateAsync();
        state.LeaseOwner.Should().Be("newer", "only its holder gives a lease back");
        state.PassCompletedAt.Should().BeNull();
    }

    private Task<GroupMigrationResult> RealFirstTouchAsync(ObjectId applicationId) =>
        InsertRaceRetry.RunInTransactionAsync(
            _factory,
            (session, token) => _realMigrator.EnsureMigratedAsync(applicationId, session, token),
            _ => { },
            Ct
        );

    [Fact]
    public async Task Run_RacingALiveFirstTouchOnTheSameGroup_LeavesOneVersionOfEach()
    {
        var ids = new List<ObjectId>();
        for (var round = 0; round < 3; round++)
        {
            var first = await ApplicationAsync(Registration(round), 2027);
            var second = await ApplicationAsync(Registration(round), 2028);
            ids.AddRange([first, second]);
            Task<GroupMigrationResult>? liveWrite = null;
            // Started once the service's transaction is open, so the two really overlap.
            _migrator.Before = id =>
            {
                liveWrite ??= RealFirstTouchAsync(second);
                return Task.CompletedTask;
            };

            await Service().RunAsync(Ct);
            await liveWrite!;
        }

        (await VersionCountAsync()).Should().Be(ids.Count);
        (await MigratedAsync()).Should().BeEquivalentTo(ids);
        (await StateAsync()).Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_TwoInstancesTogether_NeverWorkAtTheSameTimeOrRepeatAGroup()
    {
        var ids = new List<ObjectId>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await ApplicationAsync(Registration(i)));
        }

        var inFlight = 0;
        var mostAtOnce = 0;
        _migrator.Before = async _ =>
        {
            var now = Interlocked.Increment(ref inFlight);
            Interlocked.Exchange(ref mostAtOnce, Math.Max(mostAtOnce, now));
            await Task.Delay(40, Ct);
        };
        _migrator.After = _ => Interlocked.Decrement(ref inFlight);

        await Task.WhenAll(Service().RunAsync(Ct), Service().RunAsync(Ct));

        mostAtOnce.Should().Be(1);
        _migrator.Calls.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(ids);
        (await MigratedAsync()).Should().BeEquivalentTo(ids);
        var state = await StateAsync();
        state.Failures.Should().BeEmpty();
        state.LeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task Run_ACleanPassJustNow_IsNotRepeatedUntilTheWindowPasses()
    {
        var window = TimeSpan.FromMinutes(10);
        await ApplicationAsync(Registration(1));
        await Service(recentPassWindow: window).RunAsync(Ct);
        var seededAfterwards = await ApplicationAsync(Registration(2));
        _migrator.Calls.Clear();

        await Service(recentPassWindow: window).RunAsync(Ct);
        _migrator.Calls.Should().BeEmpty();

        _clock.Advance(window);
        await Service(recentPassWindow: window).RunAsync(Ct);
        _migrator.Calls.Should().Equal(seededAfterwards);
    }

    [Fact]
    public async Task Run_APassThatLeftFailures_IsAlwaysRepeated()
    {
        var window = TimeSpan.FromMinutes(10);
        var failing = await ApplicationAsync(Registration(1));
        _migrator.Before = _ => throw new InvalidOperationException();
        await Service(recentPassWindow: window).RunAsync(Ct);
        _migrator.Before = null;

        await Service(recentPassWindow: window).RunAsync(Ct);

        (await MigratedAsync()).Should().Contain(failing);
    }

    [Fact]
    public void LeaseDuration_OutlastsTheSlowestGroup() =>
        Service(clock: _clock)
            .LeaseDuration.Should()
            .BeGreaterThan(TimeSpan.FromMinutes(2) * InsertRaceRetry.MaxAttempts);

    [Fact]
    public async Task Run_ALegacyIdThatIsNotAnObjectId_IsCountedAndDoesNotStopTheRun()
    {
        await _factory
            .GetCollection<BsonDocument>("accreditationApplications")
            .InsertOneAsync(
                new BsonDocument { { "_id", "not-an-object-id" }, { "year", 2027 } },
                cancellationToken: Ct
            );
        var id = await ApplicationAsync(Registration(1));

        await Service().RunAsync(Ct);

        (await MigratedAsync()).Should().Contain(id);
        _logger
            .Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Warning && e.Message.Contains("not an ObjectId")
            );
    }

    [Fact]
    public async Task Run_SchemaNotChecked_WaitsBecauseTheUniqueIndexesMayNotExist()
    {
        var id = await ApplicationAsync(Registration(1));
        _schema.Record(HealthCheckResult.Unhealthy("not built"));

        var clock = new WaitSignallingClock(_clock);
        var running = Service(clock: clock).RunAsync(Ct);
        await clock.Waiting;

        _migrator.Calls.Should().BeEmpty();
        _schema.Record(HealthCheckResult.Degraded("validator missing"));
        await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        (await MigratedAsync()).Should().Contain(id);
    }

    [Fact]
    public async Task Start_AFailureOutsideAGroup_IsDegradedAndDoesNotStopTheHost()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        using var service = new DomainModelMigrationService(
            services,
            _schema,
            _status,
            _factory,
            _clock,
            _logger
        );

        await service.StartAsync(Ct);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        _status.Result.Status.Should().Be(HealthStatus.Degraded);
        // The provider has no migrator, so resolving one throws inside RunAsync.
        _logger
            .Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error)
            .Subject.Message.Should()
            .Contain(
                $"{nameof(InvalidOperationException)} at DomainModelMigrationService.RunAsync"
            );
    }
}
