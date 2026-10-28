using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>
/// Checks that every domain model collection has all of its indexes (phase 1, "Expand"). Run once, at startup,
/// after the collections are built, because MongoIndexInitializerService logs and carries on if index creation
/// fails. The description names collections and indexes only: no data, no PII.
/// </summary>
public static class DomainModelSchemaCheck
{
    public static async Task<HealthCheckResult> RunAsync(
        IEnumerable<IDomainModelCollection> collections,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var missing = new List<string>();
            foreach (var collection in collections)
            {
                var actual = await collection.GetIndexNamesAsync(cancellationToken);
                var absent = collection.GetExpectedIndexNames().Except(actual).ToList();
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
