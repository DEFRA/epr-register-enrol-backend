using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EprRegisterEnrolBackend.Test.Health;

public class HealthCheckEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthCheckEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Get_health_returns_200()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Head_health_returns_200()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Trace_health_returns_405()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Trace, "/health"));

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task Other_verbs_return_405(string verb)
    {
        var response = await _client.SendAsync(
            new HttpRequestMessage(new HttpMethod(verb), "/health")
        );

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }
}
