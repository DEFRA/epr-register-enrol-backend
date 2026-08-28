using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolBackend.Test.Auth;

// Exercises the Frontend auth gate end-to-end through the real ASP.NET Core auth/authz
// pipeline, outside its Development bypass — the unit tests in
// FrontendAuthenticationHandlerTests cover the handler in isolation, but nothing else in the
// suite proves the gate is actually wired onto these routes (the existing endpoint tests all
// run under AccreditationApplicationTestFactory's Development environment, where the handler's
// own dev-mode bypass auto-authenticates every request regardless of whether the gate exists).
//
// Runs on the assembly's ephemeral mongod (via EphemeralMongoTestFactory): the auth gate is
// upstream of persistence, but this factory builds the full host, so MongoIndexInitializerService
// (and any request that clears the gate) would otherwise block on a ~30s server-selection
// timeout with no Mongo reachable.
public class FrontendAuthenticationIntegrationTests : IDisposable
{
    private const string ValidSecret = "integration-test-frontend-secret";
    private readonly EphemeralMongoTestFactory _factory;
    private readonly HttpClient _client;

    public FrontendAuthenticationIntegrationTests(MongoIntegrationFixture fixture)
    {
        _factory = new EphemeralMongoTestFactory(
            fixture,
            "frontend_auth",
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["AUTH_SHARED_SECRET:FRONTEND"] = ValidSecret,
                ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = "integration-test-case-management-secret",
                ["CaseWorking:UseStub"] = "true",
            });
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task DefraLink_MissingAuthorization_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/organisations/50002/defra-link", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DefraLink_ValidBearerToken_PassesAuth()
    {
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ValidSecret);

        var response = await _client.GetAsync("/api/v1/organisations/50002/defra-link", TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DefraLink_WrongBearerToken_Returns401()
    {
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-secret");

        var response = await _client.GetAsync("/api/v1/organisations/50002/defra-link", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AccreditationApplications_GetById_MissingAuthorization_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/accreditation-applications/50002/app-1", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AccreditationApplications_GetById_ValidBearerToken_PassesAuth()
    {
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ValidSecret);

        var response = await _client.GetAsync("/api/v1/accreditation-applications/50002/app-1", TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AccreditationApplications_Withdraw_MissingAuthorization_Returns401()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/50002/app-1/withdraw",
            new { reason = "test" },
            cancellationToken: TestContext.Current.CancellationToken
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
            new { queryNote = "test", sectionKeys = Array.Empty<string>() },
            cancellationToken: TestContext.Current.CancellationToken
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
            new { },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    // Route-specific 401 tests above only cover 3 of the ~20 routes FrontendOnly(...) wraps in
    // AccreditationApplicationEndpoints — a single accidental removal of one FrontendOnly(...)
    // wrap on any of the untested routes would silently reopen the vulnerability this PR closes,
    // with nothing else in the suite catching it. This enumerates every mapped route under
    // api/v1/accreditation-applications directly from routing metadata and asserts each one
    // (other than the two case-management/* routes and the CDP webhook, which use different
    // auth) requires the Frontend authentication scheme — so removing a wrap fails this test
    // regardless of which route it was on.
    [Fact]
    public void AllAccreditationApplicationRoutes_ExceptCaseManagementAndUploadWebhook_RequireFrontendScheme()
    {
        using var scope = _factory.Services.CreateScope();
        var endpointDataSource = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();

        var routes = endpointDataSource
            .Endpoints.OfType<RouteEndpoint>()
            .Where(e =>
                e.RoutePattern.RawText?.StartsWith(
                    "api/v1/accreditation-applications",
                    StringComparison.Ordinal
                ) == true
            )
            .Where(e => !e.RoutePattern.RawText!.Contains("case-management"))
            .Where(e =>
                e.RoutePattern.RawText != "api/v1/accreditation-applications/files/upload-completed"
            )
            // RA-448: regulator/caseworker actions, same CaseManagement scheme as the two
            // case-management/* routes above (AC7) - not something the operator frontend calls.
            .Where(e =>
                e.RoutePattern.RawText
                != "api/v1/accreditation-applications/{organisationId}/{applicationId}/registration-number"
            )
            .Where(e =>
                e.RoutePattern.RawText
                != "api/v1/accreditation-applications/{organisationId}/{applicationId}/accreditation-number"
            )
            // RA-469 AC18: regulator/caseworker correction of an overseas site's recycling
            // operation codes, same CaseManagement scheme as the two routes above - called by
            // management-be, not the operator frontend.
            .Where(e =>
                e.RoutePattern.RawText
                != "api/v1/accreditation-applications/{organisationId}/{applicationId}/overseas-sites/{siteId}/recycling-operations"
            )
            .ToList();

        routes.Should().NotBeEmpty();

        foreach (var route in routes)
        {
            var policy = route.Metadata.GetMetadata<AuthorizationPolicy>();
            policy
                .Should()
                .NotBeNull($"route '{route.RoutePattern.RawText}' should require authorization");
            policy!
                .AuthenticationSchemes.Should()
                .Contain(
                    FrontendAuthenticationHandler.SchemeName,
                    $"route '{route.RoutePattern.RawText}' should require the Frontend scheme"
                );
        }
    }
}
