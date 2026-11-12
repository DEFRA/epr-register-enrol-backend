using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.Core;

namespace EprRegisterEnrolBackend.Test.DomainModel.Persistence;

/// <summary>
/// The migration path for the empty schema: with <c>DomainModel:ExpandEnabled</c> set, deploying the app creates it
/// and readiness reports it; without the flag, nothing changes.
/// </summary>
public sealed class DomainModelStartupTests(MongoIntegrationFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> s_expandEnabled = new()
    {
        ["DomainModel:ExpandEnabled"] = "true",
    };

    [Fact]
    public async Task InitializeAsync_ExpandEnabled_CreatesTheSchemaAndReadinessPasses()
    {
        using var factory = new EphemeralMongoTestFactory(
            fixture,
            "domainstartup",
            settings: s_expandEnabled
        );
        _ = factory.CreateClient();
        var initializer = factory
            .Services.GetServices<IHostedService>()
            .OfType<MongoIndexInitializerService>()
            .Single();

        await initializer.InitializeAsync(Ct);

        var collections = factory.Services.GetServices<IDomainModelCollection>().ToList();
        collections
            .Select(c => c.CollectionName)
            .Should()
            .BeEquivalentTo(
                "organisations",
                "registrationVersions",
                "overseasSiteVersions",
                "applicationVersions",
                "accreditationVersions",
                "migrationConflicts"
            );
        foreach (var collection in collections)
        {
            (await collection.GetIndexNamesAsync(Ct))
                .Should()
                .BeEquivalentTo(collection.GetExpectedIndexNames(), collection.CollectionName);
        }

        var report = await factory
            .Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == "domain-model-schema", Ct);
        report.Status.Should().Be(HealthStatus.Healthy);
        report.Entries.Should().ContainKey("domain-model-schema");
    }

    [Fact]
    public void HealthChecks_ExpandEnabled_SchemaCheckGatesReadiness()
    {
        using var factory = new EphemeralMongoTestFactory(
            fixture,
            "domainstartup",
            settings: s_expandEnabled
        );

        var registration = factory
            .Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Single(r => r.Name == "domain-model-schema");

        registration.Tags.Should().Contain("ready");
    }

    [Fact]
    public void Services_ExpandNotEnabled_RegisterNoCollectionsAndNoSchemaCheck()
    {
        using var factory = new EphemeralMongoTestFactory(fixture, "domainstartup");

        factory.Services.GetServices<IDomainModelCollection>().Should().BeEmpty();
        factory
            .Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Should()
            .NotContain(r => r.Name == "domain-model-schema");
    }

    [Fact]
    public async Task InitializeAsync_CollectionsRegistered_BuildsEachAndRecordsTheSchemaCheck()
    {
        var resolved = 0;
        var status = new DomainModelSchemaStatus();
        var services = LegacyPersistences()
            .AddSingleton<IDomainModelSchemaStatus>(status)
            .AddSingleton(_ =>
            {
                resolved++;
                return Collection("organisations", ["reExOrganisationId_u"]);
            })
            .AddSingleton(_ =>
            {
                resolved++;
                return Collection("registrationVersions", ["entityId_version_u"]);
            })
            .BuildServiceProvider();
        var logger = new CapturingLogger<MongoIndexInitializerService>();

        await new MongoIndexInitializerService(services, logger).InitializeAsync(Ct);

        resolved.Should().Be(2);
        logger.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
        status.Result.Status.Should().Be(HealthStatus.Healthy);
    }

    // A validator the database user may not apply (no collMod permission) won't appear on a retry, so it is reported as
    // Degraded after one more attempt and the startup loop stops, instead of retrying with backoff forever.
    [Fact]
    public async Task InitializeAsync_ValidatorNotPermitted_RecordsDegradedAndDoesNotRetry()
    {
        var status = new DomainModelSchemaStatus();
        var withoutValidator = ValidatedCollection(_ => ValidatorApplyResult.NotPermitted);
        var services = LegacyPersistences()
            .AddSingleton<IDomainModelSchemaStatus>(status)
            .AddSingleton(_ => withoutValidator)
            .BuildServiceProvider();

        var ready = await FastValidatorRetries(services).InitializeAsync(Ct);

        ready.Should().BeTrue();
        status.Result.Status.Should().Be(HealthStatus.Degraded);
        withoutValidator.Received(1).TryApplyValidator();
    }

    // RA-758: a brief Mongo error during collMod (a failover, a dropped connection) is retried with backoff, so the
    // validator is back on within about 30 seconds instead of staying off until the next restart.
    [Fact]
    public async Task InitializeAsync_ValidatorFailsBrieflyThenApplies_RecordsHealthy()
    {
        var applied = false;
        var attempts = 0;
        var status = new DomainModelSchemaStatus();
        var collection = ValidatedCollection(
            _ =>
            {
                if (++attempts < 2)
                {
                    return ValidatorApplyResult.Failed;
                }

                applied = true;
                return ValidatorApplyResult.Applied;
            },
            () => applied
        );
        var services = LegacyPersistences()
            .AddSingleton<IDomainModelSchemaStatus>(status)
            .AddSingleton(_ => collection)
            .BuildServiceProvider();

        var ready = await FastValidatorRetries(services).InitializeAsync(Ct);

        ready.Should().BeTrue();
        attempts.Should().Be(2);
        status.Result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task InitializeAsync_ValidatorKeepsFailing_StopsAfterTheBackoffAndRecordsDegraded()
    {
        var status = new DomainModelSchemaStatus();
        var collection = ValidatedCollection(_ => ValidatorApplyResult.Failed);
        var services = LegacyPersistences()
            .AddSingleton<IDomainModelSchemaStatus>(status)
            .AddSingleton(_ => collection)
            .BuildServiceProvider();
        var logger = new CapturingLogger<MongoIndexInitializerService>();
        var initializer = FastValidatorRetries(services, logger);

        var ready = await initializer.InitializeAsync(Ct);

        ready.Should().BeTrue();
        status.Result.Status.Should().Be(HealthStatus.Degraded);
        collection.Received(initializer.ValidatorRetryDelays.Count).TryApplyValidator();
        logger
            .Entries.Should()
            .Contain(e =>
                e.LogLevel == LogLevel.Warning && e.Message.Contains("applicationVersions")
            );
    }

    [Fact]
    public async Task InitializeAsync_ValidatorMissingButIndexesFine_DoesNotRetryAnyOtherCollection()
    {
        var status = new DomainModelSchemaStatus();
        var withValidator = ValidatedCollection(_ => ValidatorApplyResult.Failed);
        var without = Collection("organisations", ["reExOrganisationId_u"]);
        var services = LegacyPersistences()
            .AddSingleton<IDomainModelSchemaStatus>(status)
            .AddSingleton(_ => withValidator)
            .AddSingleton(_ => without)
            .BuildServiceProvider();

        await FastValidatorRetries(services).InitializeAsync(Ct);

        without.DidNotReceive().TryApplyValidator();
    }

    [Fact]
    public async Task InitializeAsync_HostStoppingDuringTheValidatorBackoff_EndsQuietly()
    {
        var status = new DomainModelSchemaStatus();
        var collection = ValidatedCollection(_ => ValidatorApplyResult.Failed);
        var services = LegacyPersistences()
            .AddSingleton<IDomainModelSchemaStatus>(status)
            .AddSingleton(_ => collection)
            .BuildServiceProvider();
        var initializer = new MongoIndexInitializerService(
            services,
            new CapturingLogger<MongoIndexInitializerService>()
        )
        {
            ValidatorRetryDelays = [TimeSpan.FromMinutes(1)],
        };
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var initialize = () => initializer.InitializeAsync(stopping.Token);

        (await initialize.Should().NotThrowAsync()).Which.Should().BeTrue();
        collection.DidNotReceive().TryApplyValidator();
    }

    [Fact]
    public void ValidatorRetryDelays_ByDefault_BackOffForAboutThirtySeconds()
    {
        var initializer = new MongoIndexInitializerService(
            new ServiceCollection().BuildServiceProvider(),
            new CapturingLogger<MongoIndexInitializerService>()
        );

        initializer
            .ValidatorRetryDelays.Should()
            .Equal(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(15)
            );
    }

    [Fact]
    public async Task InitializeAsync_BuildingACollectionFails_LeavesReadinessUnhealthy()
    {
        var status = new DomainModelSchemaStatus();
        var services = LegacyPersistences()
            .AddSingleton<IDomainModelSchemaStatus>(status)
            .AddSingleton<IDomainModelCollection>(_ =>
                throw new TimeoutException("server selection timed out")
            )
            .BuildServiceProvider();
        var logger = new CapturingLogger<MongoIndexInitializerService>();

        await new MongoIndexInitializerService(services, logger).InitializeAsync(Ct);

        logger.Entries.Should().Contain(e => e.LogLevel == LogLevel.Error);
        status.Result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task RetrySchemaAsync_MongoBackAfterAStartupOutage_ReadinessRecovers()
    {
        var attempts = 0;
        var status = new DomainModelSchemaStatus();
        var services = LegacyPersistences()
            .AddSingleton<IDomainModelSchemaStatus>(status)
            .AddSingleton(_ =>
                ++attempts < 3
                    ? throw new TimeoutException("server selection timed out")
                    : Collection("organisations", ["reExOrganisationId_u"])
            )
            .BuildServiceProvider();
        var initializer = new MongoIndexInitializerService(
            services,
            new CapturingLogger<MongoIndexInitializerService>()
        )
        {
            FirstRetryDelay = TimeSpan.FromMilliseconds(1),
        };

        (await initializer.InitializeAsync(Ct)).Should().BeFalse();
        status.Result.Status.Should().Be(HealthStatus.Unhealthy);

        await initializer.RetrySchemaAsync(Ct);

        attempts.Should().Be(3);
        status.Result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task RetrySchemaAsync_IndexesMissingThenPresent_ReadinessRecovers()
    {
        var checks = 0;
        IReadOnlyList<string> expected = ["entityId_version_u"];
        var collection = Substitute.For<IDomainModelCollection>();
        collection.CollectionName.Returns("registrationVersions");
        collection.GetExpectedIndexNames().Returns(expected);
        collection
            .GetIndexNamesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(++checks < 2 ? [] : expected));
        var status = new DomainModelSchemaStatus();
        var services = LegacyPersistences()
            .AddSingleton<IDomainModelSchemaStatus>(status)
            .AddSingleton(collection)
            .BuildServiceProvider();
        var initializer = new MongoIndexInitializerService(
            services,
            new CapturingLogger<MongoIndexInitializerService>()
        )
        {
            FirstRetryDelay = TimeSpan.FromMilliseconds(1),
        };

        (await initializer.InitializeAsync(Ct)).Should().BeFalse();
        status.Result.Description.Should().Contain("entityId_version_u");

        await initializer.RetrySchemaAsync(Ct);

        status.Result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task RetrySchemaAsync_HostStopping_EndsWithoutThrowing()
    {
        var services = LegacyPersistences()
            .AddSingleton<IDomainModelSchemaStatus>(new DomainModelSchemaStatus())
            .AddSingleton<IDomainModelCollection>(_ => throw new TimeoutException())
            .BuildServiceProvider();
        var initializer = new MongoIndexInitializerService(
            services,
            new CapturingLogger<MongoIndexInitializerService>()
        );
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var retry = () => initializer.RetrySchemaAsync(stopping.Token);

        await retry.Should().NotThrowAsync();
    }

    private static IServiceCollection LegacyPersistences() =>
        new ServiceCollection()
            .AddSingleton(Substitute.For<IAccreditationApplicationPersistence>())
            .AddSingleton(Substitute.For<IRecyclingOperationsAuditPersistence>())
            .AddSingleton(Substitute.For<IPendingUploadService>())
            .AddSingleton(Substitute.For<ICaseManagementAuthNonceStore>());

    private static MongoIndexInitializerService FastValidatorRetries(
        IServiceProvider services,
        CapturingLogger<MongoIndexInitializerService>? logger = null
    ) =>
        new(services, logger ?? new CapturingLogger<MongoIndexInitializerService>())
        {
            ValidatorRetryDelays = Enumerable.Repeat(TimeSpan.FromMilliseconds(1), 5).ToList(),
        };

    // An applicationVersions collection whose indexes are all present and whose validator is missing until
    // isApplied says otherwise. apply stands in for TryApplyValidator.
    private static IDomainModelCollection ValidatedCollection(
        Func<CallInfo, ValidatorApplyResult> apply,
        Func<bool>? isApplied = null
    )
    {
        var expected = new BsonDocument("validator", new BsonDocument("required", "name"));
        var collection = Collection("applicationVersions", ["entityId_version_u"]);
        collection.GetExpectedValidation().Returns(expected);
        collection
            .GetValidationAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(isApplied?.Invoke() == true ? expected : null));
        collection.TryApplyValidator().Returns(apply);
        return collection;
    }

    private static IDomainModelCollection Collection(string name, IReadOnlyList<string> indexes)
    {
        var collection = Substitute.For<IDomainModelCollection>();
        collection.CollectionName.Returns(name);
        collection.GetExpectedIndexNames().Returns(indexes);
        collection
            .GetIndexNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(indexes));
        return collection;
    }
}
