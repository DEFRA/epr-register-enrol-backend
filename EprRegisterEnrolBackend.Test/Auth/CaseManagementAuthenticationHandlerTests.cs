using System.Text.Encodings.Web;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.Auth;

public class CaseManagementAuthenticationHandlerTests
{
    private const string TestClientId = "epr-register-enrol-management-be";
    private const string TestSecret = "test-shared-secret";

    private static async Task<AuthenticateResult> AuthenticateAsync(
        HttpContext context,
        string? sharedSecret = TestSecret,
        bool isDevelopment = false,
        IMemoryCache? cache = null
    )
    {
        var options = new StaticOptionsMonitor<CaseManagementAuthenticationOptions>(new());
        var authConfig = Options.Create(
            new CaseManagementAuthConfig
            {
                SharedSecret = sharedSecret,
                ExpectedCognitoClientId = TestClientId,
            }
        );
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(
            isDevelopment ? Environments.Development : Environments.Production
        );

        var handler = new CaseManagementAuthenticationHandler(
            options,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            authConfig,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            environment
        );

        var scheme = new AuthenticationScheme(
            CaseManagementAuthenticationHandler.SchemeName,
            CaseManagementAuthenticationHandler.SchemeName,
            typeof(CaseManagementAuthenticationHandler)
        );
        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    private static HttpContext CreateValidRequestContext(
        string? clientId = TestClientId,
        string? secret = TestSecret,
        string? timestampOverride = null,
        string? nonceOverride = null,
        string? signatureOverride = null
    )
    {
        var context = new DefaultHttpContext();
        var timestamp = timestampOverride ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var nonce = nonceOverride ?? Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var signature =
            signatureOverride
            ?? (
                secret is null
                    ? "invalid"
                    : CaseManagementAuthenticationHandler.ComputeSignature(
                        secret,
                        clientId ?? TestClientId,
                        "jane@example.com",
                        "Jane Smith",
                        null,
                        timestamp,
                        nonce
                    )
            );

        if (clientId is not null)
            context.Request.Headers["x-cdp-cognito-client-id"] = clientId;
        context.Request.Headers["x-cdp-user-id"] = "jane@example.com";
        context.Request.Headers["x-cdp-user-name"] = "Jane Smith";
        context.Request.Headers["x-cdp-auth-signature"] = signature;
        context.Request.Headers["x-cdp-auth-timestamp"] = timestamp;
        context.Request.Headers["x-cdp-auth-nonce"] = nonce;

        return context;
    }

    [Fact]
    public async Task ValidSignature_Succeeds()
    {
        var context = CreateValidRequestContext();
        var result = await AuthenticateAsync(context);
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task MissingClientIdHeader_Fails()
    {
        var context = CreateValidRequestContext(clientId: null);
        var result = await AuthenticateAsync(context);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task MissingSignatureHeader_Fails()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-cdp-cognito-client-id"] = TestClientId;
        context.Request.Headers["x-cdp-auth-timestamp"] = DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ssZ"
        );
        context.Request.Headers["x-cdp-auth-nonce"] = Convert.ToBase64String(
            Guid.NewGuid().ToByteArray()
        );
        // x-cdp-auth-signature intentionally omitted.

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task MissingTimestampHeader_Fails()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-cdp-cognito-client-id"] = TestClientId;
        context.Request.Headers["x-cdp-auth-signature"] = "irrelevant-timestamp-missing";
        context.Request.Headers["x-cdp-auth-nonce"] = Convert.ToBase64String(
            Guid.NewGuid().ToByteArray()
        );
        // x-cdp-auth-timestamp intentionally omitted.

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task MissingNonceHeader_Fails()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-cdp-cognito-client-id"] = TestClientId;
        context.Request.Headers["x-cdp-auth-signature"] = "irrelevant-nonce-missing";
        context.Request.Headers["x-cdp-auth-timestamp"] = DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ssZ"
        );
        // x-cdp-auth-nonce intentionally omitted.

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task UnrecognisedClientId_Fails()
    {
        var context = new DefaultHttpContext();
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var signature = CaseManagementAuthenticationHandler.ComputeSignature(
            TestSecret,
            "some-other-client",
            null,
            null,
            null,
            timestamp,
            nonce
        );
        context.Request.Headers["x-cdp-cognito-client-id"] = "some-other-client";
        context.Request.Headers["x-cdp-auth-signature"] = signature;
        context.Request.Headers["x-cdp-auth-timestamp"] = timestamp;
        context.Request.Headers["x-cdp-auth-nonce"] = nonce;

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task TamperedSignature_Fails()
    {
        var context = CreateValidRequestContext(signatureOverride: "not-the-real-signature");
        var result = await AuthenticateAsync(context);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ExpiredTimestamp_Fails()
    {
        var staleTimestamp = DateTime
            .UtcNow.AddMinutes(-10)
            .ToString("yyyy-MM-ddTHH:mm:ssZ");
        var context = CreateValidRequestContext(timestampOverride: staleTimestamp);

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task UnparseableTimestamp_Fails()
    {
        // Caught by the DateTime.TryParse guard, which runs before signature verification —
        // distinct from ExpiredTimestamp_Fails (a validly-formatted but stale timestamp).
        var context = CreateValidRequestContext(timestampOverride: "not-a-timestamp");

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ReplayedNonce_SecondRequestFails()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var signature = CaseManagementAuthenticationHandler.ComputeSignature(
            TestSecret,
            TestClientId,
            "jane@example.com",
            "Jane Smith",
            null,
            timestamp,
            nonce
        );

        var firstContext = CreateValidRequestContext(
            timestampOverride: timestamp,
            nonceOverride: nonce,
            signatureOverride: signature
        );
        var firstResult = await AuthenticateAsync(firstContext, cache: cache);
        firstResult.Succeeded.Should().BeTrue();

        var secondContext = CreateValidRequestContext(
            timestampOverride: timestamp,
            nonceOverride: nonce,
            signatureOverride: signature
        );
        var secondResult = await AuthenticateAsync(secondContext, cache: cache);

        secondResult.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task MissingSharedSecret_OutsideDevelopment_FailsClosed()
    {
        var context = CreateValidRequestContext();
        var result = await AuthenticateAsync(context, sharedSecret: null, isDevelopment: false);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task MissingSharedSecret_InDevelopment_Succeeds()
    {
        var context = new DefaultHttpContext();
        var result = await AuthenticateAsync(context, sharedSecret: null, isDevelopment: true);
        result.Succeeded.Should().BeTrue();
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
