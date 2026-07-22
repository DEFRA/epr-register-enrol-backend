using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Auth;

// Verifies inbound pushes from ManagementBe (RA-311 OBE-2), the reverse direction of
// HttpCaseWorkingApiAdapter's own outbound signing. Recomputes the same v2 canonical-payload
// HMAC-SHA256 signature ManagementBe's CognitoClientIdAuthenticationHandler produces, with
// clock-skew bounding and single-use nonce replay protection.
public class CaseManagementAuthenticationHandler(
    IOptionsMonitor<CaseManagementAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<CaseManagementAuthConfig> authConfig,
    IMemoryCache nonceCache,
    IHostEnvironment environment
) : AuthenticationHandler<CaseManagementAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "CaseManagement";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var config = authConfig.Value;

        // Fail closed everywhere except Development — mirrors the outbound adapter's own
        // dev-mode bypass when CaseWorkingApiConfig.SharedSecret is empty.
        if (string.IsNullOrEmpty(config.SharedSecret))
        {
            if (!environment.IsDevelopment())
                return Task.FromResult(
                    AuthenticateResult.Fail("CaseManagement shared secret is not configured.")
                );

            var devPrincipal = new ClaimsPrincipal(new ClaimsIdentity(SchemeName));
            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(devPrincipal, SchemeName))
            );
        }

        if (!Request.Headers.TryGetValue("x-cdp-cognito-client-id", out var clientIdValues))
            return Task.FromResult(
                AuthenticateResult.Fail("Missing x-cdp-cognito-client-id header.")
            );

        var clientId = clientIdValues.ToString();
        if (!string.Equals(clientId, config.ExpectedCognitoClientId, StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.Fail("Unrecognised x-cdp-cognito-client-id."));

        if (!Request.Headers.TryGetValue("x-cdp-auth-signature", out var signatureValues))
            return Task.FromResult(AuthenticateResult.Fail("Missing x-cdp-auth-signature header."));
        if (!Request.Headers.TryGetValue("x-cdp-auth-timestamp", out var timestampValues))
            return Task.FromResult(AuthenticateResult.Fail("Missing x-cdp-auth-timestamp header."));
        if (!Request.Headers.TryGetValue("x-cdp-auth-nonce", out var nonceValues))
            return Task.FromResult(AuthenticateResult.Fail("Missing x-cdp-auth-nonce header."));

        var signature = signatureValues.ToString();
        var timestamp = timestampValues.ToString();
        var nonce = nonceValues.ToString();
        var userId = Request.Headers.TryGetValue("x-cdp-user-id", out var userIdValues)
            ? userIdValues.ToString()
            : null;
        var userName = Request.Headers.TryGetValue("x-cdp-user-name", out var userNameValues)
            ? userNameValues.ToString()
            : null;

        if (
            !DateTime.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var requestTime
            )
        )
            return Task.FromResult(AuthenticateResult.Fail("Invalid x-cdp-auth-timestamp header."));

        if ((DateTime.UtcNow - requestTime).Duration() > ClockSkew)
            return Task.FromResult(
                AuthenticateResult.Fail("Request timestamp is outside the allowed clock-skew window.")
            );

        var expectedSignature = ComputeSignature(
            config.SharedSecret,
            clientId,
            userId,
            userName,
            null,
            timestamp,
            nonce
        );
        var signatureValid = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature),
            Encoding.UTF8.GetBytes(expectedSignature)
        );
        if (!signatureValid)
            return Task.FromResult(AuthenticateResult.Fail("Invalid signature."));

        var nonceCacheKey = $"case-management-auth-nonce:{nonce}";
        if (nonceCache.TryGetValue(nonceCacheKey, out _))
            return Task.FromResult(AuthenticateResult.Fail("Nonce has already been used."));
        nonceCache.Set(nonceCacheKey, true, ClockSkew);

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, clientId) };
        if (!string.IsNullOrEmpty(userId))
            claims.Add(new Claim("cdp_user_id", userId));
        if (!string.IsNullOrEmpty(userName))
            claims.Add(new Claim("cdp_user_name", userName));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName))
        );
    }

    // Verification-side counterpart of HttpCaseWorkingApiAdapter.ComputeSignature (v2 canonical
    // payload) — the reverse direction of the same scheme ManagementBe's
    // CognitoClientIdAuthenticationHandler uses. Must stay in sync — any change is breaking.
    internal static string ComputeSignature(
        string sharedSecret,
        string clientId,
        string? userId,
        string? userName,
        string? userRoles,
        string timestamp,
        string nonce
    )
    {
        var payload = string.Join(
            '\n',
            "v2",
            clientId,
            userId ?? string.Empty,
            userName ?? string.Empty,
            userRoles ?? string.Empty,
            timestamp,
            nonce
        );
        var keyBytes = Encoding.UTF8.GetBytes(sharedSecret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var mac = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToBase64String(mac);
    }
}
