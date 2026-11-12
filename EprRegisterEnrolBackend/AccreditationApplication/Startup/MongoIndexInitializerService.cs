using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// Forces the Mongo-backed persistences to be constructed once at startup so
// their MongoService.EnsureIndexes runs then, not lazily on whichever request
// first resolves the singleton. Without this, the first accreditation request
// after a deploy pays the index-build cost on its own thread — and if Mongo is
// unreachable at that moment the reconciler's server-selection timeout blocks
// that request, then every subsequent one (MS DI does not cache a singleton
// whose constructor threw), until Mongo comes back.
//
// A BackgroundService (not a blocking IHostedService.StartAsync), matching
// RegulatoryNumberSequenceBackfillService's posture: the generic host awaits
// StartAsync before it considers the app "started", so a blocking
// implementation that threw on a transient Mongo issue at boot would crash-loop
// the whole backend. The try/catch means a failure here logs and is retried on
// the next request / redeploy instead.
//
// When the domain model schema is enabled it also builds those collections and
// then records the schema check that DomainModelSchemaHealthCheck reports, so
// a readiness probe only reads a stored result and never calls Mongo itself.
// If building them or the check fails (Mongo briefly unreachable at boot), it
// retries in the background with backoff until the check passes, so readiness
// recovers without a restart. A retry re-resolves the collections; for one
// whose constructor threw (MS DI does not cache it) that re-runs its index
// creation. A collection that was built but had an index skipped by the
// reconciler (it logs and carries on) is only re-checked, so readiness stays
// unhealthy, naming the missing index, until the next deploy.
//
// The resolution runs on a pool thread wrapped in WaitAsync(stoppingToken): on
// shutdown the awaiting caller returns promptly instead of holding the host open
// for the full server-selection timeout (the WebApplicationFactory-based tests
// depend on this). The synchronous resolve on the pool thread cannot itself be
// aborted mid-flight, so a blocked Mongo call keeps running there until it times
// out — harmless, since nothing waits on it, but it is not truly cancellable.
public class MongoIndexInitializerService(
    IServiceProvider serviceProvider,
    ILogger<MongoIndexInitializerService> logger
) : BackgroundService
{
    // Backoff for the schema retry: doubles from FirstRetryDelay up to MaxRetryDelay. Settable for tests.
    internal TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is never held up by anything below.
        await Task.Yield();
        if (!await InitializeAsync(stoppingToken))
        {
            await RetrySchemaAsync(stoppingToken);
        }
    }

    // Extracted so tests can await it deterministically — BackgroundService's
    // StartAsync only kicks ExecuteAsync off as a background task. Returns false
    // when the domain model schema is enabled but its check has not passed yet.
    internal async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(
                    () =>
                    {
                        // Resolving each singleton runs its MongoService<T>
                        // constructor, which is where EnsureIndexes lives.
                        _ = serviceProvider.GetRequiredService<IAccreditationApplicationPersistence>();
                        _ = serviceProvider.GetRequiredService<IRecyclingOperationsAuditPersistence>();
                        _ = serviceProvider.GetRequiredService<IPendingUploadService>();
                        _ = serviceProvider.GetRequiredService<ICaseManagementAuthNonceStore>();
                    },
                    cancellationToken
                )
                .WaitAsync(cancellationToken);

            logger.LogInformation("MongoIndexInitializerService: index initialisation complete");
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down before initialisation finished — nothing to do.
            return true;
        }
        catch (Exception ex)
        {
            // Best-effort: a Mongo hiccup here must not crash-loop the backend.
            // The indexes will be (re)built the next time a request resolves the
            // persistence, or on the next deploy.
            logger.LogError(ex, "MongoIndexInitializerService: index initialisation failed");
        }

        try
        {
            return await TryBuildSchemaAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    // RA-758: a validator the constructor could not apply is tried again after each of these delays, about 30
    // seconds in all, so a brief Mongo error during collMod (a failover, a dropped connection) does not leave it off
    // until the next restart. Settable for tests.
    internal IReadOnlyList<TimeSpan> ValidatorRetryDelays { get; init; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15),
    ];

    // Re-applies each validator that is missing or different, rechecking the schema after every round, until the
    // check is no longer Degraded or the delays run out. A NotPermitted result stops it at once: a database user that
    // may not run collMod will not gain the permission between retries. Each new result is recorded as it comes, so
    // readiness reflects the latest state while this runs.
    private async Task<HealthCheckResult> RetryValidatorsAsync(
        IReadOnlyList<IDomainModelCollection> collections,
        IDomainModelSchemaStatus status,
        CancellationToken cancellationToken
    )
    {
        var result = status.Result;
        foreach (var delay in ValidatorRetryDelays)
        {
            await Task.Delay(delay, cancellationToken);

            var notPermitted = false;
            foreach (
                var collection in await WithoutTheirValidatorAsync(collections, cancellationToken)
            )
            {
                var applied = await Task.Run(collection.TryApplyValidator, cancellationToken)
                    .WaitAsync(cancellationToken);
                notPermitted |= applied == ValidatorApplyResult.NotPermitted;
            }

            result = await DomainModelSchemaCheck.RunAsync(collections, cancellationToken);
            status.Record(result);
            if (result.Status != HealthStatus.Degraded)
            {
                logger.LogInformation(
                    "MongoIndexInitializerService: domain model validators applied after retrying"
                );
                return result;
            }

            if (notPermitted)
            {
                return result;
            }
        }

        logger.LogWarning(
            "MongoIndexInitializerService: domain model validators still not applied after retrying for {Duration}. {Description}",
            TimeSpan.FromTicks(ValidatorRetryDelays.Sum(d => d.Ticks)),
            result.Description
        );
        return result;
    }

    private static async Task<List<IDomainModelCollection>> WithoutTheirValidatorAsync(
        IReadOnlyList<IDomainModelCollection> collections,
        CancellationToken cancellationToken
    )
    {
        var missing = new List<IDomainModelCollection>();
        foreach (var collection in collections)
        {
            if (
                collection.GetExpectedValidation() is { } expected
                && await collection.GetValidationAsync(cancellationToken) != expected
            )
            {
                missing.Add(collection);
            }
        }

        return missing;
    }

    // Retries the domain model schema until its check passes or the host stops.
    internal async Task RetrySchemaAsync(CancellationToken cancellationToken)
    {
        var delay = FirstRetryDelay;
        try
        {
            do
            {
                logger.LogWarning(
                    "MongoIndexInitializerService: domain model schema not ready, retrying in {Delay}",
                    delay
                );
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
            } while (!await TryBuildSchemaAsync(cancellationToken));

            logger.LogInformation(
                "MongoIndexInitializerService: domain model schema ready after retrying"
            );
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down — stop retrying.
        }
    }

    // Domain model (phase 1, "Expand"): creates the empty collections and indexes,
    // then records the schema check. With DomainModel:ExpandEnabled off none are
    // registered, so there is nothing to do.
    private async Task<bool> TryBuildSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            var collections = await Task.Run(
                    () => serviceProvider.GetServices<IDomainModelCollection>().ToList(),
                    cancellationToken
                )
                .WaitAsync(cancellationToken);
            if (collections.Count == 0)
            {
                return true;
            }

            var status = serviceProvider.GetRequiredService<IDomainModelSchemaStatus>();
            var result = await DomainModelSchemaCheck.RunAsync(collections, cancellationToken);
            status.Record(result);
            if (result.Status == HealthStatus.Degraded)
            {
                result = await RetryValidatorsAsync(collections, status, cancellationToken);
            }

            // Only missing indexes keep the outer loop retrying. A validator still missing after RetryValidatorsAsync
            // is Degraded and left until the next startup (RA-746, RA-758).
            return result.Status != HealthStatus.Unhealthy;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "MongoIndexInitializerService: domain model schema could not be built"
            );
            serviceProvider
                .GetRequiredService<IDomainModelSchemaStatus>()
                .Record(
                    HealthCheckResult.Unhealthy(
                        $"Domain model collections could not be built ({ex.GetType().Name})."
                    )
                );
            return false;
        }
    }
}
