using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

public static class DomainModelSchemaStatusExtensions
{
    /// <summary>
    /// Waits until the collections and unique indexes exist. Degraded is enough: it only means the validator is missing.
    /// </summary>
    public static async Task WaitUntilBuiltAsync(
        this IDomainModelSchemaStatus status,
        TimeSpan pollEvery,
        TimeProvider clock,
        CancellationToken cancellationToken
    )
    {
        while (status.Result.Status == HealthStatus.Unhealthy)
        {
            await Task.Delay(pollEvery, clock, cancellationToken);
        }
    }
}
