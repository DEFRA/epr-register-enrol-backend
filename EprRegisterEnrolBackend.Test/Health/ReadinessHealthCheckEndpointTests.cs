using System.Net;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolBackend.Test.Health;

// HealthCheckEndpointTests exercises /health under the default WebApplicationFactory
// environment (Development), where every required-config check is either satisfied by
// appsettings.Development.json or exempted outright — so it proves nothing about the
// non-Development path RequiredConfigHealthCheck actually exists for (RA-441). These
// tests exercise that path directly with a real Production environment.
public class ReadinessHealthCheckEndpointTests
{
    private static readonly Dictionary<string, string?> CompleteConfig = new()
    {
        ["ReExApi:BaseUrl"] = "http://reex.test",
        ["REEX_API_BASIC_AUTH_USERNAME"] = "user",
        ["REEX_API_BASIC_AUTH_PASSWORD"] = "pass",
        ["App:BaseUrl"] = "http://app.test",
        ["CdpUploader:Url"] = "http://uploader.test",
        ["CaseWorking:UseStub"] = "false",
        ["CaseWorking:Url"] = "http://case-working.test",
        ["CASE_MANAGEMENT_API_SHARED_SECRET"] = "case-working-secret",
        ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = "case-management-secret",
        ["AUTH_SHARED_SECRET:FRONTEND"] = "frontend-secret",
    };

    [Fact]
    public async Task Health_ReturnsHealthy_RegardlessOfConfig()
    {
        await using var factory = new ProductionFactory(
            new Dictionary<string, string?> { ["ReExApi:BaseUrl"] = "" }
        );
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthReady_ReturnsHealthy_WhenAllRequiredConfigPresent()
    {
        await using var factory = new ProductionFactory(CompleteConfig);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthReady_ReturnsUnhealthy_WhenRequiredConfigMissing()
    {
        var incomplete = new Dictionary<string, string?>(CompleteConfig) { ["App:BaseUrl"] = "" };
        await using var factory = new ProductionFactory(incomplete);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("App__BaseUrl");
    }

    private sealed class ProductionFactory(IReadOnlyDictionary<string, string?> configOverrides)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureAppConfiguration(
                (_, config) => config.AddInMemoryCollection(configOverrides)
            );
            builder.ConfigureServices(services =>
            {
                // RA-448: this file exercises RequiredConfigHealthCheck specifically, not
                // RegulatoryNumberBackfillHealthCheck - without this, HealthReady_ReturnsHealthy
                // would fail regardless of config, since the real backfill has no live Mongo
                // to actually seed against in this test environment and never completes.
                var status = new RegulatoryNumberBackfillStatus();
                status.MarkComplete();
                services.AddSingleton<IRegulatoryNumberBackfillStatus>(status);
            });
        }
    }
}
