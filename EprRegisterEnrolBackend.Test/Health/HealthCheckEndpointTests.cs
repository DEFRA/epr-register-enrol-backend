using System.Net;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Xunit;

namespace EprRegisterEnrolBackend.Test.Health;

// Runs on the assembly's ephemeral mongod: this builds the full host, so
// MongoIndexInitializerService would otherwise block teardown on a ~30s
// server-selection timeout with no Mongo reachable.
public class HealthCheckEndpointTests : IDisposable
{
    private readonly EphemeralMongoTestFactory _factory;
    private readonly HttpClient _client;

    public HealthCheckEndpointTests(MongoIntegrationFixture fixture)
    {
        _factory = new EphemeralMongoTestFactory(fixture, "health");
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Get_health_returns_200()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Head_health_returns_200()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Trace_health_returns_405()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Trace, "/health"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task Other_verbs_return_405(string verb)
    {
        var response = await _client.SendAsync(
            new HttpRequestMessage(new HttpMethod(verb), "/health"),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }
}
