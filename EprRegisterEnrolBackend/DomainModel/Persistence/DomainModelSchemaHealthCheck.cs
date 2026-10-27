using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>
/// Keeps <c>/health/ready</c> unhealthy until every domain model collection has all of its indexes (phase 1,
/// "Expand"). MongoIndexInitializerService logs and carries on if index creation fails, so this is what makes
/// a missing or half-built schema visible in an environment. Resolving the persistence classes here also retries
/// a construction that failed earlier. The description names collections and indexes only: no data, no PII.
/// </summary>
public class DomainModelSchemaHealthCheck(IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var missing = new List<string>();
            foreach (var collection in services.GetServices<IDomainModelCollection>())
            {
                var actual = await collection.GetIndexNamesAsync(cancellationToken);
                var absent = collection.ExpectedIndexNames.Except(actual).ToList();
                if (absent.Count > 0)
                {
                    missing.Add($"{collection.CollectionName}: {string.Join(", ", absent)}");
                }
            }

            return missing.Count == 0
                ? HealthCheckResult.Healthy("Domain model collections and indexes are present.")
                : HealthCheckResult.Unhealthy(
                    $"Domain model indexes missing. {string.Join("; ", missing)}"
                );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Domain model schema could not be checked ({ex.GetType().Name})."
            );
        }
    }
}
