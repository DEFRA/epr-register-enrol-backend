using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolBackend.Test.Auth;

// Exercises the Frontend auth gate end-to-end through the real ASP.NET Core auth/authz
// pipeline, outside its Development bypass — the unit tests in
// FrontendAuthenticationHandlerTests cover the handler in isolation, but nothing else in the
// suite proves the gate is actually wired onto these routes (the existing endpoint tests all
// run under AccreditationApplicationTestFactory's Development environment, where the handler's
// own dev-mode bypass auto-authenticates every request regardless of whether the gate exists).
public class FrontendAuthenticationIntegrationTests : IClassFixture<ProductionFactory>
{
    private const string ValidSecret = "integration-test-frontend-secret";
    private readonly HttpClient _client;

    public FrontendAuthenticationIntegrationTests(ProductionFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task DefraLink_MissingAuthorization_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/organisations/50002/defra-link");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DefraLink_ValidBearerToken_PassesAuth()
    {
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ValidSecret);

        var response = await _client.GetAsync("/api/v1/organisations/50002/defra-link");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DefraLink_WrongBearerToken_Returns401()
    {
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-secret");

        var response = await _client.GetAsync("/api/v1/organisations/50002/defra-link");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AccreditationApplications_GetById_MissingAuthorization_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/accreditation-applications/50002/app-1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AccreditationApplications_GetById_ValidBearerToken_PassesAuth()
    {
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ValidSecret);

        var response = await _client.GetAsync("/api/v1/accreditation-applications/50002/app-1");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AccreditationApplications_Withdraw_MissingAuthorization_Returns401()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/50002/app-1/withdraw",
            new { reason = "test" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CaseManagementRoute_FrontendBearerToken_StillRejected()
    {
        // Confirms scheme isolation: a valid Frontend secret must not satisfy the separate
        // CaseManagement scheme these routes require instead.
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ValidSecret);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/case-management/work-item-1/query",
            new { queryNote = "test", sectionKeys = Array.Empty<string>() }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UploadCompleted_CdpWebhook_NotGated()
    {
        // The one deliberately-unauthenticated route in this group (CDP Uploader's callback
        // has no way to send our Frontend secret) — asserts it stays that way rather than
        // relying on nobody adding FrontendOnly(...) here by mistake later.
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/files/upload-completed",
            new { }
        );

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}

public class ProductionFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        builder.ConfigureAppConfiguration(
            (_, config) =>
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AUTH_SHARED_SECRET:FRONTEND"] = "integration-test-frontend-secret",
                        ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = "integration-test-cm-secret",
                        ["CaseWorking:UseStub"] = "true",
                    }
                )
        );
    }
}
