using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>
/// Keeps <c>/health/ready</c> unhealthy until the startup schema check has found every domain model collection
/// with all of its indexes. It only reads the recorded result: it never builds a collection or calls Mongo, so a
/// probe cannot block. Registered only when <c>DomainModel:ExpandEnabled</c> is set.
/// </summary>
public class DomainModelSchemaHealthCheck(IDomainModelSchemaStatus status) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(status.Result);
}
