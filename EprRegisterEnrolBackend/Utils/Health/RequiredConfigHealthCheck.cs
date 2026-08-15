using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.FileUpload.Config;
using EprRegisterEnrolBackend.ReEx.Config;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Utils.Health;

// Surfaces missing required config (e.g. an env var CDP never provisioned for an
// environment) as an unhealthy /health response, so the platform catches a broken
// deploy at rollout time instead of it showing up later as an unexplained runtime
// error on whichever request first exercises the unconfigured dependency (RA-441).
public class RequiredConfigHealthCheck(
    IOptions<ReExConfig> reExConfig,
    IOptions<ReExCredentials> reExCredentials,
    IOptions<AppConfig> appConfig,
    IOptions<CdpUploaderConfig> cdpUploaderConfig,
    IOptions<S3Config> s3Config,
    IOptions<CaseWorkingApiConfig> caseWorkingConfig,
    IHostEnvironment environment
) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        var missing = new List<string>();

        // Development wires up StubReExApiAdapter instead of the real HTTP client
        // (see Program.cs), so ReEx config is never load-bearing there.
        if (!environment.IsDevelopment())
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
        if (string.IsNullOrWhiteSpace(s3Config.Value.Region))
            missing.Add("S3__Region");
        // Url is only load-bearing when the real HttpCaseWorkingApiAdapter is
        // wired up — StubCaseWorkingApiAdapter (the default) never calls out.
        if (!caseWorkingConfig.Value.UseStub && string.IsNullOrWhiteSpace(caseWorkingConfig.Value.Url))
            missing.Add("CaseWorking__Url");

        return Task.FromResult(
            missing.Count == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(
                    $"Missing required config: {string.Join(", ", missing)}"
                )
        );
    }
}