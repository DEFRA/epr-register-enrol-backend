using System.Net;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolBackend.Test.Startup;

// RA-463: UseHsts only emits Strict-Transport-Security when it believes the request
// arrived over HTTPS. In Development the middleware is skipped entirely (see Program.cs),
// so HealthCheckEndpointTests' default WebApplicationFactory environment can't observe this
// header at all - it needs the Production environment, exercised the same way
// ReadinessHealthCheckEndpointTests does for RequiredConfigHealthCheck.
public class HstsMiddlewareTests(MongoIntegrationFixture fixture)
{
    [Fact]
    public async Task Response_IncludesStrictTransportSecurity_WhenRequestIsHttpsInProduction()
    {
        // HstsMiddleware excludes "localhost" (and other loopback hosts) by default, which is
        // WebApplicationFactory's default BaseAddress - so the host must be overridden too, not
        // just the scheme, or the header is silently skipped for a reason unrelated to this test.
        await using var factory = new ProductionFactory(fixture);
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://app.test") }
        );

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.Headers.TryGetValues("Strict-Transport-Security", out var values).Should().BeTrue();
        values!.Should().ContainSingle().Which.Should().Contain("max-age=");
    }

    [Fact]
    public async Task Response_OmitsStrictTransportSecurity_WhenRequestIsHttpInProduction()
    {
        // TLS terminates upstream in CDP: without ASPNETCORE_FORWARDEDHEADERS_ENABLED (or an
        // equivalent UseForwardedHeaders() call) actually taking effect, every request reaches
        // this middleware as plain HTTP and UseHsts silently no-ops - this is exactly the failure
        // mode flagged in PR #178, so it's worth pinning down as a test rather than only a
        // Dockerfile comment.
        await using var factory = new ProductionFactory(fixture);
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://app.test") }
        );

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    private sealed class ProductionFactory(MongoIntegrationFixture fixture) : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("hsts");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureServices(services =>
            {
                services.UseEphemeralMongoPersistence(fixture, _databaseName);

                var status = new RegulatoryNumberBackfillStatus();
                status.MarkComplete();
                services.AddSingleton<IRegulatoryNumberBackfillStatus>(status);
            });
        }
    }
}
