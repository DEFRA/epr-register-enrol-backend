using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Auth;

// Verifies inbound calls from epr-register-enrol-frontend, the only production caller of the
// ReEx-backed organisation endpoints (e.g. GetDefraLink). On non-prod environments with
// TestOrganisations__Enabled, the epr-register-enrol-mgmt-tests E2E suite is accepted too, with
// its own secret (FrontendAuthConfig.MgmtTestsSharedSecret). Unlike CaseManagementAuthenticationHandler
// this guards a plain server-to-server GET rather than a webhook-style push, so it's a static
// shared-secret comparison — no signature/nonce/replay machinery needed.
public class FrontendAuthenticationHandler(
    IOptionsMonitor<FrontendAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<FrontendAuthConfig> authConfig,
    IHostEnvironment environment
) : AuthenticationHandler<FrontendAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "Frontend";

    public const string FrontendCallerId = "epr-register-enrol-frontend";
    public const string MgmtTestsCallerId = "epr-register-enrol-mgmt-tests";

    private const string BearerPrefix = "Bearer ";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var config = authConfig.Value;

        AuthenticateResult Fail(string reason)
        {
            Logger.LogWarning("Frontend auth failed: {Reason}", reason);
            return AuthenticateResult.Fail(reason);
        }

        // Fail closed everywhere except Development — mirrors CaseManagementAuthenticationHandler's
        // dev-mode bypass so a vanilla local run works without provisioning a secret.
        if (string.IsNullOrEmpty(config.SharedSecret))
        {
            if (!environment.IsDevelopment())
            {
                Logger.LogError(
                    "Frontend auth misconfigured: shared secret is not configured in environment '{Environment}'.",
                    environment.EnvironmentName
                );
                return Task.FromResult(
                    AuthenticateResult.Fail("Frontend shared secret is not configured.")
                );
            }

            var devPrincipal = new ClaimsPrincipal(new ClaimsIdentity(SchemeName));
            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(devPrincipal, SchemeName))
            );
        }

        if (!Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
            return Task.FromResult(Fail("Missing Authorization header."));

        var authHeader = authHeaderValues.ToString();
        if (!authHeader.StartsWith(BearerPrefix, StringComparison.Ordinal))
            return Task.FromResult(Fail("Authorization header is not a Bearer token."));

        var callerId = MatchCaller(authHeader[BearerPrefix.Length..], config);
        if (callerId is null)
            return Task.FromResult(Fail("Invalid shared secret."));

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, callerId)], SchemeName)
        );
        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName))
        );
    }

    // Both comparisons always run (no short-circuit) and each is constant-time, so response
    // timing reveals neither secret nor which caller a near-miss resembled.
    private static string? MatchCaller(string providedSecret, FrontendAuthConfig config)
    {
        var provided = Encoding.UTF8.GetBytes(providedSecret);
        var isFrontend = SecretMatches(provided, config.SharedSecret);
        var isMgmtTests = SecretMatches(provided, config.MgmtTestsSharedSecret);

        if (isFrontend)
            return FrontendCallerId;
        return isMgmtTests ? MgmtTestsCallerId : null;
    }

    private static bool SecretMatches(byte[] provided, string? expected) =>
        !string.IsNullOrEmpty(expected)
        && CryptographicOperations.FixedTimeEquals(provided, Encoding.UTF8.GetBytes(expected));
}
