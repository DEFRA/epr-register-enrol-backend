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
using NSubstitute;

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
                "accreditationVersions"
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

    private static IServiceCollection LegacyPersistences() =>
        new ServiceCollection()
            .AddSingleton(Substitute.For<IAccreditationApplicationPersistence>())
            .AddSingleton(Substitute.For<IRecyclingOperationsAuditPersistence>())
            .AddSingleton(Substitute.For<IPendingUploadService>())
            .AddSingleton(Substitute.For<ICaseManagementAuthNonceStore>());

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
