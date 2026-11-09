namespace EprRegisterEnrolBackend.DevSeed;

/// <summary>
/// A Development-only background service that writes local manual-testing data once at startup.
/// Best-effort: it never holds up host startup, and a failure is logged rather than thrown, so a
/// Mongo hiccup cannot crash-loop the local backend.
/// </summary>
public abstract class DevSeedService(ILogger logger) : BackgroundService
{
    protected ILogger Logger { get; } = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is never held up by anything below.
        await Task.Yield();
        await SeedAsync(stoppingToken);
    }

    // Extracted so tests can await it deterministically.
    internal async Task SeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SeedCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down before seeding finished - nothing to do.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{SeedService}: seeding failed", GetType().Name);
        }
    }

    protected abstract Task SeedCoreAsync(CancellationToken cancellationToken);
}
