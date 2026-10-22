using System.Security.Claims;
using System.Text.Encodings.Web;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.Auth;

public class ServiceClientAuthenticationHandlerTests
{
    private const string ReExClientId = "epr-backend";
    private const string ReExSecret = "reex-test-secret";
    private const string OtherClientId = "another-consumer";
    private const string OtherSecret = "another-consumer-secret";

    private readonly FakeCaseManagementAuthNonceStore _nonces = new();
    private readonly CapturingLoggerFactory _logs = new();

    private IServiceClientRegistry Registry(
        Dictionary<string, string?>? extraSettings = null
    )
    {
        var settings = new Dictionary<string, string?>
        {
            ["AUTH_SHARED_SECRET:REEX"] = ReExSecret,
            ["AUTH_SHARED_SECRET:OTHER"] = OtherSecret,
        };
        foreach (var (key, value) in extraSettings ?? [])
            settings[key] = value;

        return new ServiceClientRegistry(
            Options.Create(
                new ServiceClientsConfig
                {
                    Clients =
                    [
                        new ServiceClientDefinition { Name = "reex", ClientId = ReExClientId },
                        new ServiceClientDefinition { Name = "other", ClientId = OtherClientId },
                    ],
                }
            ),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<ServiceClientRegistry>.Instance
        );
    }

    private async Task<(AuthenticateResult Result, HttpContext Context)> AuthenticateAsync(
        Dictionary<string, string> headers,
        IServiceClientRegistry? registry = null,
        ICaseManagementAuthNonceStore? nonces = null
    )
    {
        var context = new DefaultHttpContext();
        foreach (var (name, value) in headers)
            context.Request.Headers[name] = value;

        var handler = new ServiceClientAuthenticationHandler(
            new StaticOptionsMonitor<ServiceClientAuthenticationOptions>(new()),
            _logs,
            UrlEncoder.Default,
            registry ?? Registry(),
            nonces ?? _nonces
        );
        await handler.InitializeAsync(
            new AuthenticationScheme(
                ServiceClientAuthenticationHandler.SchemeName,
                ServiceClientAuthenticationHandler.SchemeName,
                typeof(ServiceClientAuthenticationHandler)
            ),
            context
        );
        return (await handler.AuthenticateAsync(), context);
    }

    private static string FailureCode(HttpContext context) =>
        ((ValueTuple<string, string>)context.Items[ServiceClientAuthenticationHandler.FailureItemKey]!)
            .Item1;

    [Fact]
    public async Task ValidRequest_Succeeds_AsTheClient_WithReadScopeOnly()
    {
        var (result, _) = await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, ReExSecret)
        );

        result.Succeeded.Should().BeTrue();
        var principal = result.Principal!;
        principal.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(ReExClientId);
        principal.FindFirstValue(ServiceClientAuthenticationHandler.ClientNameClaimType)
            .Should()
            .Be("reex");
        principal
            .FindAll(ServiceClientAuthenticationHandler.ScopeClaimType)
            .Select(c => c.Value)
            .Should()
            .Equal(ServiceClientAuthenticationHandler.ReadScope);
    }

    [Theory]
    [InlineData("x-cdp-client-id")]
    [InlineData("x-cdp-auth-signature")]
    [InlineData("x-cdp-auth-timestamp")]
    [InlineData("x-cdp-auth-nonce")]
    public async Task MissingHeader_IsRefused_NamingTheHeader(string header)
    {
        var headers = ServiceClientSigner.Headers(ReExClientId, ReExSecret);
        headers.Remove(header);

        var (result, context) = await AuthenticateAsync(headers);

        result.Succeeded.Should().BeFalse();
        FailureCode(context).Should().Be(ServiceClientAuthFailure.MissingHeader);
        result.Failure!.Message.Should().Contain(header);
    }

    [Fact]
    public async Task UnknownClientId_IsRefused()
    {
        var (result, context) = await AuthenticateAsync(
            ServiceClientSigner.Headers("nobody", ReExSecret)
        );

        result.Succeeded.Should().BeFalse();
        FailureCode(context).Should().Be(ServiceClientAuthFailure.UnknownClient);
    }

    [Fact]
    public async Task ManagementBeCredentials_AreNotAcceptedHere()
    {
        var (result, context) = await AuthenticateAsync(
            ServiceClientSigner.Headers("epr-register-enrol-management-be", "management-be-secret")
        );

        result.Succeeded.Should().BeFalse();
        FailureCode(context).Should().Be(ServiceClientAuthFailure.UnknownClient);
    }

    [Fact]
    public async Task WrongSecret_IsRefused()
    {
        var (result, context) = await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, "not-the-secret")
        );

        result.Succeeded.Should().BeFalse();
        FailureCode(context).Should().Be(ServiceClientAuthFailure.InvalidSignature);
    }

    [Fact]
    public async Task TamperedSignature_IsRefused()
    {
        var (result, context) = await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, ReExSecret, signatureOverride: "AAAA")
        );

        result.Succeeded.Should().BeFalse();
        FailureCode(context).Should().Be(ServiceClientAuthFailure.InvalidSignature);
    }

    [Fact]
    public async Task OneClientsSecret_CannotBeUsedToPassAsAnotherClient()
    {
        // Signed with the other client's own (valid) secret, but claiming to be ReEx.
        var (asReEx, reExContext) = await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, OtherSecret)
        );
        // And the reverse.
        var (asOther, otherContext) = await AuthenticateAsync(
            ServiceClientSigner.Headers(OtherClientId, ReExSecret)
        );

        asReEx.Succeeded.Should().BeFalse();
        FailureCode(reExContext).Should().Be(ServiceClientAuthFailure.InvalidSignature);
        asOther.Succeeded.Should().BeFalse();
        FailureCode(otherContext).Should().Be(ServiceClientAuthFailure.InvalidSignature);
    }

    [Fact]
    public async Task SignatureForOneClientId_IsNotValidWhenTheHeaderNamesAnother()
    {
        // The signature covers the client id, so swapping only the header breaks it even when
        // two clients happened to share a secret.
        var sharedSecret = "same-secret";
        var registry = Registry(
            new()
            {
                ["AUTH_SHARED_SECRET:REEX"] = sharedSecret,
                ["AUTH_SHARED_SECRET:OTHER"] = sharedSecret,
            }
        );
        var headers = ServiceClientSigner.Headers(ReExClientId, sharedSecret);
        headers["x-cdp-client-id"] = OtherClientId;

        var (result, context) = await AuthenticateAsync(headers, registry);

        result.Succeeded.Should().BeFalse();
        FailureCode(context).Should().Be(ServiceClientAuthFailure.InvalidSignature);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(6)]
    [InlineData(-120)]
    public async Task Timestamp_OutsideTheFiveMinuteWindow_IsExpired(int minutesFromNow)
    {
        var (result, context) = await AuthenticateAsync(
            ServiceClientSigner.Headers(
                ReExClientId,
                ReExSecret,
                timestamp: ServiceClientSigner.Timestamp(DateTime.UtcNow.AddMinutes(minutesFromNow))
            )
        );

        result.Succeeded.Should().BeFalse();
        FailureCode(context).Should().Be(ServiceClientAuthFailure.ExpiredTimestamp);
    }

    [Fact]
    public async Task Timestamp_JustInsideTheWindow_IsAccepted()
    {
        var (result, _) = await AuthenticateAsync(
            ServiceClientSigner.Headers(
                ReExClientId,
                ReExSecret,
                timestamp: ServiceClientSigner.Timestamp(DateTime.UtcNow.AddMinutes(-4))
            )
        );

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task UnparseableTimestamp_IsRefused()
    {
        var (result, context) = await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, ReExSecret, timestamp: "yesterday-ish")
        );

        result.Succeeded.Should().BeFalse();
        FailureCode(context).Should().Be(ServiceClientAuthFailure.InvalidTimestamp);
    }

    [Fact]
    public async Task ReusedNonce_IsRefused()
    {
        var timestamp = ServiceClientSigner.Timestamp();
        var nonce = ServiceClientSigner.NewNonce();

        var (first, _) = await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, ReExSecret, timestamp, nonce)
        );
        var (second, context) = await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, ReExSecret, timestamp, nonce)
        );

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeFalse();
        FailureCode(context).Should().Be(ServiceClientAuthFailure.ReusedNonce);
    }

    [Fact]
    public async Task SameNonce_FromTwoClients_IsNotAClash()
    {
        var timestamp = ServiceClientSigner.Timestamp();
        var nonce = ServiceClientSigner.NewNonce();

        var (reEx, _) = await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, ReExSecret, timestamp, nonce)
        );
        var (other, _) = await AuthenticateAsync(
            ServiceClientSigner.Headers(OtherClientId, OtherSecret, timestamp, nonce)
        );

        reEx.Succeeded.Should().BeTrue();
        other.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task UnauthenticatedRequest_CannotUseUpARealClientsNonce()
    {
        var timestamp = ServiceClientSigner.Timestamp();
        var nonce = ServiceClientSigner.NewNonce();

        // Someone without the secret sends a nonce first ...
        var (forged, _) = await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, "guess", timestamp, nonce)
        );
        // ... which must not stop the real client using that nonce.
        var (real, _) = await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, ReExSecret, timestamp, nonce)
        );

        forged.Succeeded.Should().BeFalse();
        real.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Nonce_IsRemembered_LongEnoughToOutliveTheTimestampWindow()
    {
        var store = Substitute.For<ICaseManagementAuthNonceStore>();
        store
            .TryConsumeAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, ReExSecret),
            nonces: store
        );

        // A request is accepted for 5 minutes either side of its own timestamp, so a nonce has to
        // be kept for more than the 5 minutes a single window lasts, or a replay could slip through.
        await store
            .Received(1)
            .TryConsumeAsync(
                Arg.Is<string>(key => key.StartsWith($"service-client:{ReExClientId}:")),
                Arg.Is<TimeSpan>(ttl => ttl >= TimeSpan.FromMinutes(10)),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task SecretInRotation_BothTheCurrentAndTheNextSecret_AreAccepted()
    {
        var registry = Registry(new() { ["AUTH_SHARED_SECRET:REEX_NEXT"] = "next-secret" });

        var (current, _) = await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, ReExSecret),
            registry
        );
        var (next, _) = await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, "next-secret"),
            registry
        );

        current.Succeeded.Should().BeTrue();
        next.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ClientWithNoSecret_IsRefused_NotLetThrough()
    {
        var registry = new ServiceClientRegistry(
            Options.Create(
                new ServiceClientsConfig
                {
                    Clients = [new ServiceClientDefinition { Name = "reex", ClientId = ReExClientId }],
                }
            ),
            new ConfigurationBuilder().Build(),
            NullLogger<ServiceClientRegistry>.Instance
        );

        var (result, context) = await AuthenticateAsync(
            ServiceClientSigner.Headers(ReExClientId, ""),
            registry
        );

        result.Succeeded.Should().BeFalse();
        FailureCode(context).Should().Be(ServiceClientAuthFailure.UnknownClient);
    }

    [Fact]
    public async Task Logs_NameTheClient_AndNeverTheSecretOrTheSignature()
    {
        var headers = ServiceClientSigner.Headers(ReExClientId, ReExSecret);
        await AuthenticateAsync(headers);
        var wrong = ServiceClientSigner.Headers(ReExClientId, "wrong-secret-value");
        await AuthenticateAsync(wrong);

        var text = string.Join('\n', _logs.Entries.Select(entry => entry.Message));
        text.Should().Contain(ReExClientId);
        text.Should().NotContain(ReExSecret);
        text.Should().NotContain("wrong-secret-value");
        text.Should().NotContain(headers["x-cdp-auth-signature"]);
        text.Should().NotContain(wrong["x-cdp-auth-signature"]);
        text.Should().NotContain(headers["x-cdp-auth-nonce"]);
    }

    [Fact]
    public async Task ChallengeAfterAFailure_Is401ProblemJson_WithTheReason()
    {
        var headers = ServiceClientSigner.Headers(ReExClientId, "wrong");
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        foreach (var (name, value) in headers)
            context.Request.Headers[name] = value;
        var handler = new ServiceClientAuthenticationHandler(
            new StaticOptionsMonitor<ServiceClientAuthenticationOptions>(new()),
            _logs,
            UrlEncoder.Default,
            Registry(),
            _nonces
        );
        await handler.InitializeAsync(
            new AuthenticationScheme(
                ServiceClientAuthenticationHandler.SchemeName,
                ServiceClientAuthenticationHandler.SchemeName,
                typeof(ServiceClientAuthenticationHandler)
            ),
            context
        );

        await handler.AuthenticateAsync();
        await handler.ChallengeAsync(null);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.ContentType.Should().Be("application/problem+json");
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("\"code\":\"invalid_signature\"");
        body.Should().Contain("\"status\":401");
        body.Should().NotContain(ReExSecret);
    }
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
