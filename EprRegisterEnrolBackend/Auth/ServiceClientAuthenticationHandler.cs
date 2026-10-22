using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Auth;

public class ServiceClientAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Why a service client's request was refused. The <c>code</c> is part of the error contract
/// documented in the onboarding guide, so these values must not change.
/// </summary>
public static class ServiceClientAuthFailure
{
    public const string MissingHeader = "missing_header";
    public const string UnknownClient = "unknown_client";
    public const string InvalidTimestamp = "invalid_timestamp";
    public const string ExpiredTimestamp = "expired_timestamp";
    public const string InvalidSignature = "invalid_signature";
    public const string ReusedNonce = "reused_nonce";
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
}

/// <summary>
/// Verifies requests from service clients such as ReEx: the same client-id plus HMAC scheme
/// ManagementBe uses to call this backend (a v3 canonical payload signed with HMAC-SHA256), but
/// with a registry of clients, each with its own secret, instead of one. It is a separate scheme
/// from <see cref="CaseManagementAuthenticationHandler"/> on purpose: an identity authenticated
/// here carries only the <c>read</c> scope and is accepted only on the routes that ask for this
/// scheme, so it can never reach the routes ManagementBe uses to record changes, and ManagementBe's
/// credentials are not accepted here.
/// </summary>
public class ServiceClientAuthenticationHandler(
    IOptionsMonitor<ServiceClientAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceClientRegistry registry,
    ICaseManagementAuthNonceStore nonceStore
) : AuthenticationHandler<ServiceClientAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "ServiceClient";
    public const string ReadScope = "read";
    public const string ScopeClaimType = "scope";
    public const string ClientNameClaimType = "client_name";

    internal const string FailureItemKey = "ServiceClientAuth.Failure";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    // A nonce must stay recorded for as long as a request carrying it could still pass the
    // timestamp check: the window is +/- ClockSkew around the request's own timestamp, and the
    // nonce is recorded at some point inside that window, so it has to outlive 2 x ClockSkew.
    private static readonly TimeSpan NonceLifetime = ClockSkew * 2;

    private const string CorrelationIdHeaderName = "X-Correlation-Id";
    private const string AbsentCorrelationId = "(absent)";

    private static readonly JsonSerializerOptions ProblemJson = new(JsonSerializerDefaults.Web);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var correlationId = Request.Headers.TryGetValue(CorrelationIdHeaderName, out var c)
            ? c.ToString()
            : AbsentCorrelationId;

        AuthenticateResult Fail(string code, string reason, string? claimedClientId = null)
        {
            Context.Items[FailureItemKey] = (code, reason);
            // The claimed client id is logged (it is not a secret) so a refused request can be
            // traced to the client that sent it. The secret, signature and nonce never are.
            Logger.LogWarning(
                "Service client auth failed: {Code} ({Reason}) clientId={ClientId} correlationId={CorrelationId}",
                code,
                reason,
                claimedClientId ?? "(none)",
                correlationId
            );
            return AuthenticateResult.Fail(reason);
        }

        var missing = new[]
        {
            "x-cdp-client-id",
            "x-cdp-auth-signature",
            "x-cdp-auth-timestamp",
            "x-cdp-auth-nonce",
        }.FirstOrDefault(name =>
            !Request.Headers.TryGetValue(name, out var v) || string.IsNullOrWhiteSpace(v)
        );
        if (missing is not null)
        {
            return Fail(
                ServiceClientAuthFailure.MissingHeader,
                $"Missing {missing} header.",
                Request.Headers.TryGetValue("x-cdp-client-id", out var claimed)
                    ? claimed.ToString()
                    : null
            );
        }

        var clientId = Request.Headers["x-cdp-client-id"].ToString();
        var signature = Request.Headers["x-cdp-auth-signature"].ToString();
        var timestamp = Request.Headers["x-cdp-auth-timestamp"].ToString();
        var nonce = Request.Headers["x-cdp-auth-nonce"].ToString();
        var userId = Request.Headers.TryGetValue("x-cdp-user-id", out var u) ? u.ToString() : null;
        var userName = Request.Headers.TryGetValue("x-cdp-user-name", out var n)
            ? n.ToString()
            : null;

        if (!registry.TryGet(clientId, out var client))
        {
            return Fail(
                ServiceClientAuthFailure.UnknownClient,
                "Unrecognised x-cdp-client-id.",
                clientId
            );
        }

        if (
            !DateTime.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var requestTime
            )
        )
        {
            return Fail(
                ServiceClientAuthFailure.InvalidTimestamp,
                "Invalid x-cdp-auth-timestamp header.",
                clientId
            );
        }

        if ((DateTime.UtcNow - requestTime).Duration() > ClockSkew)
        {
            return Fail(
                ServiceClientAuthFailure.ExpiredTimestamp,
                "Request timestamp is outside the allowed window of 5 minutes either side of the server clock.",
                clientId
            );
        }

        // Every secret is compared, with no early exit, so how long the check takes does not
        // reveal which secret (current or next) matched.
        var signatureBytes = Encoding.UTF8.GetBytes(signature);
        var valid = false;
        foreach (var secret in client.Secrets)
        {
            var expected = CaseManagementAuthenticationHandler.ComputeSignature(
                secret,
                clientId,
                userId,
                userName,
                timestamp,
                nonce
            );
            valid |= CryptographicOperations.FixedTimeEquals(
                signatureBytes,
                Encoding.UTF8.GetBytes(expected)
            );
        }

        if (!valid)
        {
            return Fail(
                ServiceClientAuthFailure.InvalidSignature,
                "Invalid signature.",
                clientId
            );
        }

        // Recorded only after the signature has verified, so an unauthenticated caller can never
        // use up a real client's nonce, and scoped by client so two clients can never collide.
        if (
            !await nonceStore.TryConsumeAsync(
                $"service-client:{client.ClientId}:{nonce}",
                NonceLifetime,
                Context.RequestAborted
            )
        )
        {
            return Fail(
                ServiceClientAuthFailure.ReusedNonce,
                "Nonce has already been used.",
                clientId
            );
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, client.ClientId),
            new(ClientNameClaimType, client.Name),
            new(ScopeClaimType, ReadScope),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));

        Logger.LogInformation(
            "Service client auth succeeded for clientId={ClientId} correlationId={CorrelationId}",
            client.ClientId,
            correlationId
        );
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var (code, reason) =
            Context.Items.TryGetValue(FailureItemKey, out var failure)
            && failure is ValueTuple<string, string> tuple
                ? tuple
                : (ServiceClientAuthFailure.Unauthenticated, "Authentication is required.");

        Response.Headers.WWWAuthenticate = "CDP-HMAC";
        return WriteProblemAsync(Context, StatusCodes.Status401Unauthorized, code, reason);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(
            Context,
            StatusCodes.Status403Forbidden,
            ServiceClientAuthFailure.Forbidden,
            "This client is not allowed to make this request."
        );

    /// <summary>
    /// Writes the service API's error format: RFC 9457 problem details with a stable <c>code</c>
    /// and the <c>traceId</c>, as <c>application/problem+json</c>.
    /// </summary>
    internal static Task WriteProblemAsync(HttpContext context, int status, string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = status switch
            {
                StatusCodes.Status401Unauthorized => "Unauthorized",
                StatusCodes.Status403Forbidden => "Forbidden",
                StatusCodes.Status404NotFound => "Not Found",
                StatusCodes.Status400BadRequest => "Bad Request",
                _ => "Error",
            },
            Detail = detail,
            Type = $"https://httpstatuses.io/{status}",
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(problem, ProblemJson));
    }
}
