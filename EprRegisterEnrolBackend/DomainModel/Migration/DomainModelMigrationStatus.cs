using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>What readiness reports about the migration, held in memory so a probe never calls Mongo.</summary>
public interface IDomainModelMigrationStatus
{
    HealthCheckResult Result { get; }

    /// <summary>Records how many groups are failing: none is Healthy, any is Degraded.</summary>
    void RecordFailedGroups(int count);

    /// <summary>Records that a pass stopped before it finished. It carries on at the next startup.</summary>
    void RecordStopped(string errorKind);
}

public sealed class DomainModelMigrationStatus : IDomainModelMigrationStatus
{
    private volatile HealthCheckResultHolder _result = new(HealthCheckResult.Healthy());

    public HealthCheckResult Result => _result.Value;

    public void RecordFailedGroups(int count) =>
        _result = new(
            count == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded(
                    $"{count} registration group(s) could not be migrated."
                )
        );

    public void RecordStopped(string errorKind) =>
        _result = new(
            HealthCheckResult.Degraded($"The migration stopped early, error {errorKind}.")
        );

    // HealthCheckResult is a struct, which volatile cannot hold.
    private sealed record HealthCheckResultHolder(HealthCheckResult Value);
}

/// <summary>Degraded still answers 200, so a group that will not migrate is visible but never blocks a deploy.</summary>
public sealed class DomainModelMigrationHealthCheck(IDomainModelMigrationStatus status)
    : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(status.Result);
}
