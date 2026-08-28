using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Services;

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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is never held up by anything below.
        await Task.Yield();
        await InitializeAsync(stoppingToken);
    }

    // Extracted so tests can await it deterministically — BackgroundService's
    // StartAsync only kicks ExecuteAsync off as a background task.
    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(
                    () =>
                    {
                        // Resolving each singleton runs its MongoService<T>
                        // constructor, which is where EnsureIndexes lives.
                        _ =
                            serviceProvider.GetRequiredService<IAccreditationApplicationPersistence>();
                        _ =
                            serviceProvider.GetRequiredService<IRecyclingOperationsAuditPersistence>();
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
        }
        catch (Exception ex)
        {
            // Best-effort: a Mongo hiccup here must not crash-loop the backend.
            // The indexes will be (re)built the next time a request resolves the
            // persistence, or on the next deploy.
            logger.LogError(ex, "MongoIndexInitializerService: index initialisation failed");
        }
    }
}
