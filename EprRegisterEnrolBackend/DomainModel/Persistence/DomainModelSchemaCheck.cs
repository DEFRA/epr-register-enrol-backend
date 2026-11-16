using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>
/// Checks that every domain model collection has all of its indexes (phase 1, "Expand") and, where it has one, its
/// <c>$jsonSchema</c> validator in the expected mode. Run by MongoIndexInitializerService at startup, after the
/// collections are built, and again with backoff until no index is missing. A missing index is Unhealthy, which keeps
/// <c>/health/ready</c> down. A missing or different validator is only Degraded (RA-746): it is a backstop, and a
/// database user that may not run collMod must not block deploys. The description names collections, indexes and
/// validators only: no data, no PII.
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
            var validators = new List<string>();
            foreach (var collection in collections)
            {
                var actual = await collection.GetIndexNamesAsync(cancellationToken);
                var absent = collection.GetExpectedIndexNames().Except(actual).ToList();
                if (absent.Count > 0)
                {
                    missing.Add($"{collection.CollectionName}: {string.Join(", ", absent)}");
                }

                if (await ValidatorProblemAsync(collection, cancellationToken) is { } problem)
                {
                    validators.Add(problem);
                }
            }

            var validatorNote =
                validators.Count == 0
                    ? ""
                    : $" Domain model validators not as expected. {string.Join("; ", validators)}";
            if (missing.Count > 0)
            {
                return HealthCheckResult.Unhealthy(
                    $"Domain model indexes missing. {string.Join("; ", missing)}{validatorNote}"
                );
            }

            return validators.Count == 0
                ? HealthCheckResult.Healthy("Domain model collections and indexes are present.")
                : HealthCheckResult.Degraded(
                    $"Domain model collections and indexes are present.{validatorNote}"
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

    /// <summary>
    /// What is wrong with the collection's validator on the server, or null when it is as expected or the collection
    /// has none. The one place the expected and current validators are compared: the startup retry uses it too, so the
    /// two cannot drift apart. Mongo errors are thrown to the caller.
    /// </summary>
    public static async Task<string?> ValidatorProblemAsync(
        IDomainModelCollection collection,
        CancellationToken cancellationToken
    )
    {
        if (collection.GetExpectedValidation() is not { } expected)
        {
            return null;
        }

        var current = await collection.GetValidationAsync(cancellationToken);
        if (current is null)
        {
            return $"{collection.CollectionName}: no validator";
        }

        return current == expected
            ? null
            : $"{collection.CollectionName}: validator differs from the expected one";
    }
}
