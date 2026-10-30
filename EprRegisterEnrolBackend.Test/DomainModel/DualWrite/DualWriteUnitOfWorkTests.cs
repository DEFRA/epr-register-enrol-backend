using System.Diagnostics.Metrics;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolBackend.Test.DomainModel.DualWrite;

/// <summary>Phase 3's unit of work against a real single-node replica set, so the transaction is real.</summary>
public sealed class DualWriteUnitOfWorkTests : IDisposable
{
    private const string Operation = "TestOperation";
    private const string ReExRegistrationId = "6512a0f0c0ffee0000000002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly IVersionedPersistence<RegistrationEntity> _registrations;
    private readonly AccreditationApplicationPersistence _applications;
    private readonly CapturingLogger<DualWriteUnitOfWork> _logger = new();
    private readonly ServiceProvider _metricsProvider;
    private readonly IMeterFactory _meterFactory;
    private readonly MeterListener _listener = new();
    private readonly List<string?> _failedOperations = [];
    private readonly TestClock _clock = new(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
    private readonly IApplicationGroupMigrator _migrator =
        Substitute.For<IApplicationGroupMigrator>();
    private readonly IApplicationModelSync _sync = Substitute.For<IApplicationModelSync>();

    public DualWriteUnitOfWorkTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("dualwrite");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _registrations = new RegistrationPersistence(_factory, NullLoggerFactory.Instance, _clock);
        _applications = new AccreditationApplicationPersistence(
            _factory,
            NullLoggerFactory.Instance
        );

        _metricsProvider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _meterFactory = _metricsProvider.GetRequiredService<IMeterFactory>();
        // Only this test's meters: a listener sees every meter in the process, and the endpoint tests' own
        // dual-write failures run in parallel with these.
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (
                ReferenceEquals(instrument.Meter.Scope, _meterFactory)
                && instrument.Meter.Name == DualWriteMetrics.MeterName
                && instrument.Name == DualWriteMetrics.FailuresInstrument
            )
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(
            (_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == DualWriteMetrics.OperationTag)
                    {
                        _failedOperations.Add(tag.Value as string);
                    }
                }
            }
        );
        _listener.Start();
        _migrator
            .EnsureMigratedAsync(
                Arg.Any<ObjectId>(),
                Arg.Any<IClientSessionHandle>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(GroupMigrationResult.NothingToDo);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metricsProvider.Dispose();
        _factory.GetClient().DropDatabase(_databaseName);
    }

    private DualWriteUnitOfWork UnitOfWork(IApplicationGroupMigrator? migrator = null) =>
        new(_factory, migrator ?? _migrator, _sync, new DualWriteMetrics(_meterFactory), _logger);

    private async Task<AccreditationApplicationModel> SeedApplicationAsync()
    {
        var application = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-1",
            RegistrationId = ReExRegistrationId,
            MaterialType = MaterialType.Plastic,
            Year = 2027,
            ApplicationStatus = ApplicationStatus.Saved,
        };
        await _applications.CreateAsync(application);
        return application;
    }

    // One legacy write and one model write, both in the session: the shape every phase 3 write path takes.
    private async Task WriteBothAsync(
        AccreditationApplicationModel application,
        IClientSessionHandle? session,
        CancellationToken token
    )
    {
        var stored = await _applications.GetByIdAsync(
            application.OrganisationId!,
            application.Id.ToString()!,
            session
        );
        stored!.ApplicationStatus = ApplicationStatus.Started;
        Assert.NotNull(await _applications.UpdateAsync(stored, session));

        await _registrations.Versions.InsertAsync(
            new RegistrationEntity { ReExRegistrationId = ReExRegistrationId },
            new ChangeInfo { Reason = ChangeReason.ReExImport, ApplicationId = application.Id },
            session,
            token
        );
    }

    private Task<RegistrationEntity?> CurrentRegistrationAsync() =>
        _registrations.Versions.FindCurrentAsync(
            Builders<RegistrationEntity>.Filter.Eq(r => r.ReExRegistrationId, ReExRegistrationId),
            cancellationToken: Ct
        );

    private async Task<ApplicationStatus> StoredStatusAsync(
        AccreditationApplicationModel application
    ) =>
        (
            await _applications.GetByIdAsync(
                application.OrganisationId!,
                application.Id.ToString()!
            )
        )!.ApplicationStatus;

    [Fact]
    public async Task RunAsync_WorkSucceeds_CommitsBothWritesAndReturnsItsResult()
    {
        var application = await SeedApplicationAsync();

        var result = await UnitOfWork()
            .RunAsync(
                Operation,
                application.Id.ToString()!,
                async (session, token) =>
                {
                    Assert.NotNull(session);
                    await WriteBothAsync(application, session, token);
                    return Results.NoContent();
                },
                Ct
            );

        Assert.IsType<NoContent>(result);
        Assert.Equal(ApplicationStatus.Started, await StoredStatusAsync(application));
        Assert.NotNull(await CurrentRegistrationAsync());
        Assert.Empty(_logger.Entries);
        Assert.Empty(_failedOperations);
    }

    [Fact]
    public async Task RunAsync_ModelWriteFails_RollsBackTheLegacyWriteAndAnswers500()
    {
        var application = await SeedApplicationAsync();

        var result = await UnitOfWork()
            .RunAsync(
                Operation,
                application.Id.ToString()!,
                async (session, token) =>
                {
                    await WriteBothAsync(application, session, token);
                    throw new InvalidOperationException("Jane Smith at jane@example.com");
                },
                Ct
            );

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, problem.StatusCode);
        Assert.Equal(ApplicationStatus.Saved, await StoredStatusAsync(application));
        Assert.Null(await CurrentRegistrationAsync());
        Assert.Equal([Operation], _failedOperations);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.Null(entry.Exception);
        Assert.Contains(Operation, entry.Message);
        Assert.Contains(application.Id.ToString()!, entry.Message);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);
        Assert.DoesNotContain("Jane", entry.Message);
        Assert.DoesNotContain("jane@example.com", entry.Message);
    }

    [Fact]
    public async Task RunAsync_LosesAnInsertRaceOnce_RetriesAndCommits()
    {
        var collection = _factory
            .GetClient()
            .GetDatabase(_databaseName)
            .GetCollection<BsonDocument>("raceTarget");
        var taken = ObjectId.GenerateNewId();
        await collection.InsertOneAsync(new BsonDocument("_id", taken), cancellationToken: Ct);
        var attempts = 0;

        var result = await UnitOfWork()
            .RunAsync(
                Operation,
                "app-1",
                async (session, token) =>
                {
                    attempts++;
                    // The first attempt collides with the rival's committed key; the retry uses a free one.
                    var id = attempts == 1 ? taken : ObjectId.GenerateNewId();
                    await collection.InsertOneAsync(
                        session!,
                        new BsonDocument("_id", id),
                        cancellationToken: token
                    );
                    return Results.NoContent();
                },
                Ct
            );

        Assert.IsType<NoContent>(result);
        Assert.Equal(2, attempts);
        Assert.Equal(
            2,
            await collection.CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty,
                cancellationToken: Ct
            )
        );
        Assert.Equal(LogLevel.Warning, Assert.Single(_logger.Entries).LogLevel);
    }

    [Fact]
    public async Task RunAsync_LosesEveryInsertRace_GivesUpAfterMaxAttempts()
    {
        var collection = _factory
            .GetClient()
            .GetDatabase(_databaseName)
            .GetCollection<BsonDocument>("raceTarget");
        var taken = ObjectId.GenerateNewId();
        await collection.InsertOneAsync(new BsonDocument("_id", taken), cancellationToken: Ct);
        var attempts = 0;

        var result = await UnitOfWork()
            .RunAsync(
                Operation,
                "app-1",
                async (session, token) =>
                {
                    attempts++;
                    await collection.InsertOneAsync(
                        session!,
                        new BsonDocument("_id", taken),
                        cancellationToken: token
                    );
                    return Results.NoContent();
                },
                Ct
            );

        Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(InsertRaceRetry.MaxAttempts, attempts);
        Assert.Equal(
            [LogLevel.Warning, LogLevel.Warning, LogLevel.Error],
            _logger.Entries.Select(e => e.LogLevel)
        );
    }

    [Fact]
    public async Task RunAsync_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UnitOfWork()
                .RunAsync(
                    Operation,
                    "app-1",
                    (_, _) =>
                    {
                        cts.Cancel();
                        cts.Token.ThrowIfCancellationRequested();
                        return Task.FromResult(Results.NoContent());
                    },
                    cts.Token
                )
        );
        Assert.Empty(_failedOperations);
    }

    [Fact]
    public async Task RunAsync_EnsuresTheApplicationIsMigrated_InTheWorksSessionBeforeTheWork()
    {
        var applicationId = ObjectId.GenerateNewId();
        IClientSessionHandle? migratedIn = null;
        _migrator
            .EnsureMigratedAsync(
                applicationId,
                Arg.Any<IClientSessionHandle>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                migratedIn = call.Arg<IClientSessionHandle>();
                Assert.True(migratedIn.IsInTransaction);
                return GroupMigrationResult.NothingToDo;
            });

        var result = await UnitOfWork()
            .RunAsync(
                Operation,
                applicationId.ToString(),
                (session, _) =>
                {
                    Assert.NotNull(migratedIn);
                    Assert.Same(migratedIn, session);
                    return Task.FromResult(Results.NoContent());
                },
                Ct
            );

        Assert.IsType<NoContent>(result);
    }

    [Fact]
    public async Task RunAsync_ApplicationIdIsNotAnObjectId_RunsTheWorkWithoutMigrating()
    {
        var result = await UnitOfWork()
            .RunAsync(Operation, "not-an-id", (_, _) => Task.FromResult(Results.NotFound()), Ct);

        Assert.IsType<NotFound>(result);
        await _migrator
            .DidNotReceive()
            .EnsureMigratedAsync(
                Arg.Any<ObjectId>(),
                Arg.Any<IClientSessionHandle>(),
                Arg.Any<CancellationToken>()
            );
        await _sync
            .DidNotReceive()
            .SyncAsync(
                Arg.Any<ObjectId>(),
                Arg.Any<IClientSessionHandle>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RunAsync_SyncsTheModel_InTheWorksSessionAfterTheWork()
    {
        var applicationId = ObjectId.GenerateNewId();
        IClientSessionHandle? workedIn = null;
        _sync
            .SyncAsync(applicationId, Arg.Any<IClientSessionHandle>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.NotNull(workedIn);
                Assert.Same(workedIn, call.Arg<IClientSessionHandle>());
                Assert.True(workedIn.IsInTransaction);
                return Task.CompletedTask;
            });

        var result = await UnitOfWork()
            .RunAsync(
                Operation,
                applicationId.ToString(),
                (session, _) =>
                {
                    workedIn = session;
                    return Task.FromResult(Results.NoContent());
                },
                Ct
            );

        Assert.IsType<NoContent>(result);
        await _sync
            .Received(1)
            .SyncAsync(
                applicationId,
                Arg.Any<IClientSessionHandle>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RunAsync_DomainRuleBroken_RollsBackAndAnswers409NamingTheRule()
    {
        var application = await SeedApplicationAsync();

        var result = await UnitOfWork()
            .RunAsync(
                Operation,
                application.Id.ToString()!,
                async (session, token) =>
                {
                    await WriteBothAsync(application, session, token);
                    throw new DomainRuleViolationException(
                        DomainRule.YearAlreadyAccredited,
                        "Registration for Jane Smith already accredited"
                    );
                },
                Ct
            );

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal(
            nameof(DomainRule.YearAlreadyAccredited),
            problem.ProblemDetails.Extensions["rule"]
        );
        Assert.Equal(ApplicationStatus.Saved, await StoredStatusAsync(application));
        Assert.Null(await CurrentRegistrationAsync());
        Assert.Equal([Operation], _failedOperations);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.LogLevel);
        Assert.Contains(nameof(DomainRule.YearAlreadyAccredited), entry.Message);
        Assert.Contains(application.Id.ToString()!, entry.Message);
        Assert.DoesNotContain("Jane", entry.Message);
    }

    [Fact]
    public async Task RunAsync_SyncFails_RollsBackTheWorkAndAnswers500()
    {
        var application = await SeedApplicationAsync();
        _sync
            .SyncAsync(
                Arg.Any<ObjectId>(),
                Arg.Any<IClientSessionHandle>(),
                Arg.Any<CancellationToken>()
            )
            .Throws(new InvalidOperationException("model write failed"));

        var result = await UnitOfWork()
            .RunAsync(
                Operation,
                application.Id.ToString()!,
                async (session, token) =>
                {
                    await WriteBothAsync(application, session, token);
                    return Results.NoContent();
                },
                Ct
            );

        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            Assert.IsType<ProblemHttpResult>(result).StatusCode
        );
        Assert.Equal(ApplicationStatus.Saved, await StoredStatusAsync(application));
        Assert.Null(await CurrentRegistrationAsync());
        Assert.Equal([Operation], _failedOperations);
    }

    [Fact]
    public async Task RunAsync_MigrationFails_DoesNotRunTheWorkAndAnswers500()
    {
        _migrator
            .EnsureMigratedAsync(
                Arg.Any<ObjectId>(),
                Arg.Any<IClientSessionHandle>(),
                Arg.Any<CancellationToken>()
            )
            .Throws(
                new ApplicationGroupTooLargeException(
                    ReExRegistrationId,
                    ApplicationGroupMigrator.MaxGroupSize
                )
            );
        var ran = false;

        var result = await UnitOfWork()
            .RunAsync(
                Operation,
                ObjectId.GenerateNewId().ToString(),
                (_, _) =>
                {
                    ran = true;
                    return Task.FromResult(Results.NoContent());
                },
                Ct
            );

        Assert.False(ran);
        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            Assert.IsType<ProblemHttpResult>(result).StatusCode
        );
        Assert.Contains(
            nameof(ApplicationGroupTooLargeException),
            Assert.Single(_logger.Entries).Message
        );
        Assert.Equal([Operation], _failedOperations);
    }

    [Fact]
    public async Task RunAsync_WorkFailsAfterAFirstTouchMigration_RollsTheMigrationBackToo()
    {
        var application = await SeedApplicationAsync();
        var logs = NullLoggerFactory.Instance;
        var migrator = new ApplicationGroupMigrator(
            _applications,
            new ApplicationGroupStores(
                new OrganisationPersistence(_factory, logs),
                _registrations,
                new OverseasSitePersistence(_factory, logs, _clock),
                new ApplicationVersionPersistence(_factory, logs, _clock),
                new AccreditationPersistence(_factory, logs, _clock),
                new MigrationConflictPersistence(_factory, logs)
            ),
            _clock
        );

        var result = await UnitOfWork(migrator)
            .RunAsync(
                Operation,
                application.Id.ToString()!,
                async (session, token) =>
                {
                    Assert.NotNull(
                        await _registrations.Versions.FindCurrentAsync(
                            Builders<RegistrationEntity>.Filter.Eq(
                                r => r.ReExRegistrationId,
                                ReExRegistrationId
                            ),
                            session,
                            token
                        )
                    );
                    throw new InvalidOperationException("model write failed");
                },
                Ct
            );

        Assert.IsType<ProblemHttpResult>(result);
        Assert.Null(await CurrentRegistrationAsync());
        Assert.Equal(
            0,
            await _factory
                .GetCollection<ApplicationEntity>(DomainModelCollections.ApplicationVersions)
                .CountDocumentsAsync(
                    FilterDefinition<ApplicationEntity>.Empty,
                    cancellationToken: Ct
                )
        );
        Assert.Null(
            (
                await _applications.GetByIdAsync(
                    application.OrganisationId!,
                    application.Id.ToString()!
                )
            )!.RegistrationEntityId
        );
    }

    [Fact]
    public async Task LegacyOnly_RunsTheWorkWithoutASession()
    {
        var application = await SeedApplicationAsync();
        IClientSessionHandle? seen = null;
        var expected = Results.Ok();

        var result = await new LegacyOnlyUnitOfWork().RunAsync(
            Operation,
            application.Id.ToString()!,
            async (session, _) =>
            {
                seen = session;
                var stored = await _applications.GetByIdAsync(
                    application.OrganisationId!,
                    application.Id.ToString()!
                );
                stored!.ApplicationStatus = ApplicationStatus.Started;
                await _applications.UpdateAsync(stored);
                return expected;
            },
            Ct
        );

        Assert.Same(expected, result);
        Assert.Null(seen);
        Assert.Equal(ApplicationStatus.Started, await StoredStatusAsync(application));
    }
}
