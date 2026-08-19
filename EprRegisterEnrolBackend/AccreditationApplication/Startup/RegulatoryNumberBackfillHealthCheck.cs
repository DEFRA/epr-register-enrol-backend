using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-448: gates /health/ready on the counter backfill (AC4) having completed, so
// real traffic can't reach the registration/accreditation-number endpoints before
// the 16 pools are seeded - closing the race RegulatoryNumberSequenceBackfillService's
// non-blocking BackgroundService design otherwise leaves open (a request landing in
// that window would upsert a fresh counter at CurrentMax=0 and return sequence=1,
// colliding with a real already-issued number). Tagged "ready" like
// RequiredConfigHealthCheck - a gap here degrades readiness, it doesn't crash the host.
public class RegulatoryNumberBackfillHealthCheck(IRegulatoryNumberBackfillStatus status)
    : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            status.IsComplete
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(
                    "Regulatory number sequence backfill has not completed yet."
                )
        );
}
