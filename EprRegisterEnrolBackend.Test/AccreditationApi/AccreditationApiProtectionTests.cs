using System.Net;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApi;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.Test.Auth;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.AccreditationApi;

// RA-702: load protection and the error format of the accreditation API: a request limit for each
// client, the answer to a query the database stopped, and a 403 in the same format as the rest.
public class AccreditationApiProtectionTests : IDisposable
{
    private const string ReExClientId = "epr-backend";
    private const string ReExSecret = "reex-integration-secret";
    private const string OtherClientId = "another-consumer";
    private const string OtherSecret = "another-integration-secret";

    private const int Limit = 3;

    private readonly EphemeralMongoTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApiProtectionTests(MongoIntegrationFixture fixture)
    {
        _factory = new EphemeralMongoTestFactory(
            fixture,
            "accreditation_api_protection",
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["ServiceClients:Clients:0:Name"] = "reex",
                ["ServiceClients:Clients:0:ClientId"] = ReExClientId,
                ["ServiceClients:Clients:1:Name"] = "other",
                ["ServiceClients:Clients:1:ClientId"] = OtherClientId,
                ["ServiceClients:RateLimitPermitsPerMinute"] = Limit.ToString(),
                ["AUTH_SHARED_SECRET:REEX"] = ReExSecret,
                ["AUTH_SHARED_SECRET:OTHER"] = OtherSecret,
                ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = "integration-management-be-secret",
                ["AUTH_SHARED_SECRET:FRONTEND"] = "integration-frontend-secret",
                ["CaseWorking:UseStub"] = "true",
            }
        );
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> Get(
        string path,
        string clientId = ReExClientId,
        string secret = ReExSecret,
        HttpClient? client = null
    ) =>
        (client ?? _client).SendAsync(
            ServiceClientSigner.Request(HttpMethod.Get, path, clientId, secret),
            Ct
        );

    private static async Task AssertProblem(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code
    )
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        body.GetProperty("status").GetInt32().Should().Be((int)status);
        body.GetProperty("code").GetString().Should().Be(code);
        body.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ---- request limit ----

    [Fact]
    public async Task AClientOverItsLimit_Gets429_WithWhenToRetry()
    {
        for (var i = 0; i < Limit; i++)
        {
            (await Get("/v1/accreditations")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var response = await Get("/v1/accreditations");

        await AssertProblem(response, HttpStatusCode.TooManyRequests, "rate_limited");
        response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);
    }

    [Fact]
    public async Task TheLimit_IsKeptForEachClient()
    {
        for (var i = 0; i <= Limit; i++)
        {
            await Get("/v1/accreditations");
        }

        (await Get("/v1/accreditations")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var other = await Get("/v1/accreditations", OtherClientId, OtherSecret);

        other.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheLimit_AppliesToEveryRoute()
    {
        for (var i = 0; i < Limit; i++)
        {
            await Get("/v1/accreditations");
        }

        var response = await Get(
            "/v1/organisations/500027/registrations/R27EX5000270012AL/accreditations/2027"
        );

        await AssertProblem(response, HttpStatusCode.TooManyRequests, "rate_limited");
    }

    // Requests that fail authentication are not counted against the client they name, so nobody can
    // use up ReEx's allowance by sending requests in its name.
    [Fact]
    public async Task RequestsThatFailAuthentication_AreNotCountedAgainstTheClient()
    {
        for (var i = 0; i < Limit * 3; i++)
        {
            (await Get("/v1/accreditations", secret: "not-the-secret")).StatusCode
                .Should()
                .Be(HttpStatusCode.Unauthorized);
        }

        (await Get("/v1/accreditations")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void ALimitOfZero_TurnsTheLimitOff()
    {
        var limiter = new ServiceClientRateLimiter(
            Options.Create(new ServiceClientsConfig { RateLimitPermitsPerMinute = 0 })
        );

        for (var i = 0; i < 1000; i++)
        {
            limiter.TryAcquire("client", out _).Should().BeTrue();
        }
    }

    // ---- a query the database stopped ----

    [Fact]
    public async Task AQueryTheDatabaseStopped_Is503_AsAProblem()
    {
        var filter = new ServiceClientQueryTimeoutFilter(NullLoggerFactory.Instance);
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();

        var result = await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(http),
            _ => throw TimeoutException()
        );

        await ((IResult)result!).ExecuteAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        http.Response.ContentType.Should().Be("application/problem+json");
        http.Response.Headers.RetryAfter.ToString().Should().NotBeNullOrEmpty();
        http.Response.Body.Position = 0;
        var body = JsonDocument.Parse(http.Response.Body).RootElement;
        body.GetProperty("code").GetString().Should().Be("query_timeout");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AnyOtherFailure_IsNotHiddenByTheTimeoutAnswer()
    {
        var filter = new ServiceClientQueryTimeoutFilter(NullLoggerFactory.Instance);

        var act = async () =>
            await filter.InvokeAsync(
                new DefaultEndpointFilterInvocationContext(new DefaultHttpContext()),
                _ => throw new InvalidOperationException("boom")
            );

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static MongoExecutionTimeoutException TimeoutException() =>
        (MongoExecutionTimeoutException)
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(MongoExecutionTimeoutException)
            );

    // ---- 403 ----

    // Every client has the read scope and the routes take GET only, so a 403 cannot be reached through
    // them. A probe route with the same policy and a POST makes it reachable, to pin its format.
    [Fact]
    public async Task AnAuthenticatedClient_RefusedByThePolicy_Gets403_AsAProblem()
    {
        using var probeFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter, ForbiddenProbeStartupFilter>()
            )
        );
        using var client = probeFactory.CreateClient();

        var response = await client.SendAsync(
            ServiceClientSigner.Request(
                HttpMethod.Post,
                "/v1/forbidden-probe",
                ReExClientId,
                ReExSecret
            ),
            Ct
        );

        await AssertProblem(response, HttpStatusCode.Forbidden, "forbidden");
    }

    private sealed class ForbiddenProbeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Map(
                    "/v1/forbidden-probe",
                    branch =>
                    {
                        branch.UseRouting();
                        branch.UseAuthorization();
                        branch.UseEndpoints(endpoints =>
                            endpoints
                                .MapPost("/", () => "never reached")
                                .RequireAuthorization(ServiceClientPolicies.Read)
                        );
                    }
                );
                next(app);
            };
    }
}
