using System.Net;
using System.Text;
using EprRegisterEnrolBackend.ReEx.Config;
using EprRegisterEnrolBackend.ReEx.Http;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Test.ReEx;

public class BasicAuthHandlerTests
{
    private static (BasicAuthHandler handler, List<HttpRequestMessage> captured) BuildSut(
        string username, string password)
    {
        var credentials = Options.Create(new ReExCredentials { Username = username, Password = password });
        var captured = new List<HttpRequestMessage>();
        var innerHandler = new CapturingHandler(req =>
        {
            captured.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new BasicAuthHandler(credentials) { InnerHandler = innerHandler };
        return (handler, captured);
    }

    [Fact]
    public async Task SendAsync_SetsBasicAuthorizationHeader()
    {
        var (handler, captured) = BuildSut("testuser", "testpass");
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://reex.example.com/api"),
            CancellationToken.None
        );

        var auth = captured[0].Headers.Authorization;
        auth.Should().NotBeNull();
        auth!.Scheme.Should().Be("Basic");

        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("testuser:testpass"));
        auth.Parameter.Should().Be(expected);
    }

    [Fact]
    public async Task SendAsync_CredentialsWithSpecialCharacters_EncodesCorrectly()
    {
        const string username = "user@domain.com";
        const string password = "p@$$w0rd!";
        var (handler, captured) = BuildSut(username, password);
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://reex.example.com/api"),
            CancellationToken.None
        );

        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        captured[0].Headers.Authorization!.Parameter.Should().Be(expected);
    }

    [Fact]
    public async Task SendAsync_EmptyCredentials_SetsHeaderWithEmptyEncoding()
    {
        var (handler, captured) = BuildSut(string.Empty, string.Empty);
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://reex.example.com/api"),
            CancellationToken.None
        );

        captured[0].Headers.Authorization.Should().NotBeNull();
        captured[0].Headers.Authorization!.Scheme.Should().Be("Basic");
    }

    [Fact]
    public async Task SendAsync_ConsistentHeaderAcrossMultipleRequests()
    {
        var (handler, captured) = BuildSut("user", "pass");
        using var invoker = new HttpMessageInvoker(handler);

        for (var i = 0; i < 3; i++)
            await invoker.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "https://reex.example.com/api"),
                CancellationToken.None
            );

        var headers = captured.Select(r => r.Headers.Authorization!.Parameter).Distinct();
        headers.Should().HaveCount(1, "all requests should carry the same encoded credentials");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handle;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) => _handle = handle;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handle(request));
    }
}
