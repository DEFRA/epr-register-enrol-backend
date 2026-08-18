using System.Text.Encodings.Web;
using EprRegisterEnrolBackend.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.Auth;

public class FrontendAuthenticationHandlerTests
{
    private const string TestSecret = "test-frontend-shared-secret";

    private static async Task<AuthenticateResult> AuthenticateAsync(
        HttpContext context,
        string? sharedSecret = TestSecret,
        bool isDevelopment = false
    )
    {
        var options = new StaticOptionsMonitor<FrontendAuthenticationOptions>(new());
        var authConfig = Options.Create(new FrontendAuthConfig { SharedSecret = sharedSecret });
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(
            isDevelopment ? Environments.Development : Environments.Production
        );

        var handler = new FrontendAuthenticationHandler(
            options,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            authConfig,
            environment
        );

        var scheme = new AuthenticationScheme(
            FrontendAuthenticationHandler.SchemeName,
            FrontendAuthenticationHandler.SchemeName,
            typeof(FrontendAuthenticationHandler)
        );
        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    private static HttpContext CreateRequestContext(string? authorizationHeader)
    {
        var context = new DefaultHttpContext();
        if (authorizationHeader is not null)
            context.Request.Headers["Authorization"] = authorizationHeader;
        return context;
    }

    [Fact]
    public async Task ValidBearerToken_Succeeds()
    {
        var context = CreateRequestContext($"Bearer {TestSecret}");

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task MissingAuthorizationHeader_Fails()
    {
        var context = CreateRequestContext(null);

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task NonBearerAuthorizationHeader_Fails()
    {
        var context = CreateRequestContext("Basic dXNlcjpwYXNz");

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task WrongSecret_Fails()
    {
        var context = CreateRequestContext("Bearer not-the-real-secret");

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task EmptyBearerToken_Fails()
    {
        var context = CreateRequestContext("Bearer ");

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task DifferentLengthSecret_Fails()
    {
        // FixedTimeEquals only guarantees constant time for equal-length inputs — still must
        // reject (not throw) when the provided secret is a different length to the real one.
        var context = CreateRequestContext("Bearer short");

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task MissingSharedSecret_OutsideDevelopment_FailsClosed()
    {
        var context = CreateRequestContext($"Bearer {TestSecret}");

        var result = await AuthenticateAsync(context, sharedSecret: null, isDevelopment: false);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task MissingSharedSecret_InDevelopment_SucceedsEvenWithoutHeader()
    {
        var context = CreateRequestContext(null);

        var result = await AuthenticateAsync(context, sharedSecret: null, isDevelopment: true);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ConfiguredSecret_InDevelopment_StillRequiresValidBearerToken()
    {
        // Development only bypasses when the secret is unconfigured (mirrors
        // CaseManagementAuthenticationHandler) — a configured secret must still be checked.
        var context = CreateRequestContext(null);

        var result = await AuthenticateAsync(context, sharedSecret: TestSecret, isDevelopment: true);

        result.Succeeded.Should().BeFalse();
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose() { }
        }
    }
}
