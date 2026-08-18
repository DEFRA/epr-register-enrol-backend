using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Auth;

// Verifies inbound calls from epr-register-enrol-frontend, the only caller of the
// ReEx-backed organisation endpoints (e.g. GetDefraLink). Unlike CaseManagementAuthenticationHandler
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

        var providedSecret = authHeader[BearerPrefix.Length..];
        var secretValid = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedSecret),
            Encoding.UTF8.GetBytes(config.SharedSecret)
        );
        if (!secretValid)
            return Task.FromResult(Fail("Invalid shared secret."));

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "epr-register-enrol-frontend")],
                SchemeName
            )
        );
        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName))
        );
    }
}
