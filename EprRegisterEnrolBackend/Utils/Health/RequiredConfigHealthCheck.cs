using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.ReEx.Config;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Utils.Health;

// Surfaces missing required config (e.g. an env var CDP never provisioned for an
// environment) as an unhealthy /health/ready response, so the platform catches a
// broken deploy at rollout time instead of it showing up later as an unexplained
// runtime error on whichever request first exercises the unconfigured dependency
// (RA-441). Tagged "ready" — kept off the plain /health liveness probe so a config
// gap degrades readiness rather than crash-looping the whole task (see Program.cs).
public class RequiredConfigHealthCheck(
    IOptions<ReExConfig> reExConfig,
    IOptions<ReExCredentials> reExCredentials,
    IOptions<AppConfig> appConfig,
    IOptions<CdpUploaderConfig> cdpUploaderConfig,
    IOptions<CaseWorkingApiConfig> caseWorkingConfig,
    IOptions<CaseManagementAuthConfig> caseManagementAuthConfig,
    IOptions<FrontendAuthConfig> frontendAuthConfig,
    IHostEnvironment environment
) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        var missing = new List<string>();

        // Development (or ReExApi:UseStub=true, e.g. perf-test) wires up
        // StubReExApiAdapter instead of the real HTTP client (see Program.cs), so
        // ReEx config is never load-bearing in either case.
        if (!environment.IsDevelopment() && !reExConfig.Value.UseStub)
        {
            if (string.IsNullOrWhiteSpace(reExConfig.Value.BaseUrl))
                missing.Add("ReExApi__BaseUrl");
            if (string.IsNullOrWhiteSpace(reExCredentials.Value.Username))
                missing.Add("REEX_API_BASIC_AUTH_USERNAME");
            if (string.IsNullOrWhiteSpace(reExCredentials.Value.Password))
                missing.Add("REEX_API_BASIC_AUTH_PASSWORD");
        }
        if (string.IsNullOrWhiteSpace(appConfig.Value.BaseUrl))
            missing.Add("App__BaseUrl");
        if (string.IsNullOrWhiteSpace(cdpUploaderConfig.Value.Url))
            missing.Add("CdpUploader__Url");

        // Url and the outbound signing secret are only load-bearing when the real
        // HttpCaseWorkingApiAdapter is wired up — StubCaseWorkingApiAdapter never calls
        // out, and CaseWorkingApiConfig.UseStub defaults to true (see appsettings.json),
        // so a real environment that never explicitly disabled it inherits the stub
        // silently. That's a worse failure than a missing URL — submissions go nowhere
        // and nothing signals it — so flag it explicitly outside Development.
        if (!caseWorkingConfig.Value.UseStub)
        {
            if (string.IsNullOrWhiteSpace(caseWorkingConfig.Value.Url))
                missing.Add("CaseWorking__Url");
            // HttpCaseWorkingApiAdapter tolerates a blank secret by sending unsigned
            // (no environment gate on the adapter side), and appsettings.Development.json
            // runs the real adapter (UseStub: false) without ever providing this secret —
            // so demanding it in Development would make /health/ready unhealthy on a
            // vanilla local run for a condition that's working exactly as designed.
            if (
                !environment.IsDevelopment()
                && string.IsNullOrWhiteSpace(caseWorkingConfig.Value.SharedSecret)
            )
                missing.Add("CASE_MANAGEMENT_API_SHARED_SECRET");
        }
        else if (!environment.IsDevelopment())
        {
            missing.Add("CaseWorking__UseStub (still defaulted to stub outside Development)");
        }

        // CaseManagementAuthenticationHandler fails closed on every inbound request when
        // this is blank outside Development — an empty secret here doesn't just break one
        // dependency, it's a live auth gap on every CaseManagement-authenticated endpoint.
        if (
            !environment.IsDevelopment()
            && string.IsNullOrWhiteSpace(caseManagementAuthConfig.Value.SharedSecret)
        )
            missing.Add("AUTH_SHARED_SECRET__MANAGEMENT_BE");

        // FrontendAuthenticationHandler fails closed on every inbound request when this is
        // blank outside Development — an empty secret here is a live auth gap on the
        // ReEx-backed organisation endpoints (e.g. defra-link).
        if (
            !environment.IsDevelopment()
            && string.IsNullOrWhiteSpace(frontendAuthConfig.Value.SharedSecret)
        )
            missing.Add("AUTH_SHARED_SECRET__FRONTEND");

        return Task.FromResult(
            missing.Count == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(
                    $"Missing required config: {string.Join(", ", missing)}"
                )
        );
    }
}