using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.DomainModel.Persistence;

/// <summary>The migration path for the empty schema: deploying the app creates it, and readiness reports it.</summary>
public sealed class DomainModelStartupTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly EphemeralMongoTestFactory _factory;

    public DomainModelStartupTests(MongoIntegrationFixture fixture)
    {
        _factory = new EphemeralMongoTestFactory(fixture, "domainstartup");
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Starting_the_app_creates_the_empty_schema_and_the_schema_health_check_passes()
    {
        _ = _factory.CreateClient();
        var initializer = _factory
            .Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<MongoIndexInitializerService>()
            .Single();
        await initializer.InitializeAsync(Ct);

        var collections = _factory.Services.GetServices<IDomainModelCollection>().ToList();
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
                .BeEquivalentTo(collection.ExpectedIndexNames, collection.CollectionName);
        }

        var report = await _factory
            .Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == "domain-model-schema", Ct);
        report.Status.Should().Be(HealthStatus.Healthy);
        report.Entries.Should().ContainKey("domain-model-schema");
    }

    [Fact]
    public void The_schema_check_gates_readiness_and_not_liveness()
    {
        var options = _factory
            .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
            .Value;

        var registration = options.Registrations.Single(r => r.Name == "domain-model-schema");

        registration.Tags.Should().Contain("ready");
    }

    [Fact]
    public async Task The_initializer_resolves_every_registered_domain_model_collection()
    {
        var resolved = 0;
        var services = new ServiceCollection()
            .AddSingleton(Substitute.For<IAccreditationApplicationPersistence>())
            .AddSingleton(Substitute.For<IRecyclingOperationsAuditPersistence>())
            .AddSingleton(Substitute.For<IPendingUploadService>())
            .AddSingleton(Substitute.For<ICaseManagementAuthNonceStore>())
            .AddSingleton<IDomainModelCollection>(_ =>
            {
                resolved++;
                return Substitute.For<IDomainModelCollection>();
            })
            .AddSingleton<IDomainModelCollection>(_ =>
            {
                resolved++;
                return Substitute.For<IDomainModelCollection>();
            })
            .BuildServiceProvider();
        var logger = new CapturingLogger<MongoIndexInitializerService>();

        await new MongoIndexInitializerService(services, logger).InitializeAsync(Ct);

        resolved.Should().Be(2);
        logger.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
    }
}
