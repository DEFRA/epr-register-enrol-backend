using EprRegisterEnrolBackend.AccreditationApplication.Services;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-448 / AC4 (launch blocker): seeds all 16 regulatoryNumberSequences counter
// keys to at least the real-world maximum observed per pool, so this endpoint's
// first-ever generated number never collides with one already issued outside
// this system. Runs once at startup; SeedIfHigherAsync never lowers an existing
// higher value, so re-running on every deploy is safe (idempotent).
//
// A BackgroundService (not a blocking IHostedService.StartAsync), matching
// DevScanAutoCompleteService's pattern: the generic host awaits StartAsync
// before it considers the app "started", so a blocking implementation that
// throws on a transient Mongo issue at boot would crash-loop the whole
// backend, not just this feature - inconsistent with how RequiredConfigHealthCheck
// degrades readiness gracefully instead of crashing. ExecuteAsync's own
// try/catch means a failure here logs and gives up gracefully instead.
//
// Being non-blocking opens a real window where Kestrel is already accepting
// traffic before the seed finishes - RegulatoryNumberBackfillHealthCheck closes
// it by keeping /health/ready unhealthy (status.IsComplete stays false) until
// SeedAllAsync actually finishes, so the platform doesn't route real traffic to
// the number-generation endpoints during that window.
//
// SEED VALUES BELOW ARE A POINT-IN-TIME SNAPSHOT (observed max + 50 buffer, from
// the 13 August 2026 public register export) - NOT a live feed. Re-derive these
// from the live production data / most recent register export before this goes
// to production, since more numbers will have been issued since the snapshot
// was taken (see the RA-448 design doc's "Starting values for the 16 counters"
// section for the full derivation).
public class RegulatoryNumberSequenceBackfillService(
    IRegulatoryNumberSequenceCounterPersistence counters,
    IRegulatoryNumberBackfillStatus status,
    ILogger<RegulatoryNumberSequenceBackfillService> logger
) : BackgroundService
{
    private static readonly IReadOnlyDictionary<string, int> SeedValues = new Dictionary<
        string,
        int
    >
    {
        ["R-ER"] = 337,
        ["R-EX"] = 341,
        ["R-NR"] = 91,
        ["R-NX"] = 92,
        ["R-SR"] = 71,
        ["R-SX"] = 69,
        ["R-WR"] = 74,
        ["R-WX"] = 73,
        ["A-ER"] = 320,
        ["A-EX"] = 318,
        ["A-NR"] = 90,
        ["A-NX"] = 91,
        ["A-SR"] = 71,
        ["A-SX"] = 69,
        ["A-WR"] = 74,
        ["A-WX"] = 73,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SeedAllAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Best-effort: a Mongo hiccup here must not crash-loop the whole backend
            // (see the class-level comment for why this is a BackgroundService, not
            // a blocking IHostedService.StartAsync). AC4's floor is still enforced by
            // SeedIfHigherAsync being safe to re-run on the next deploy/restart.
            logger.LogError(ex, "RegulatoryNumberSequenceBackfillService: seed failed");
        }
    }

    // Extracted so tests can await the seed deterministically - BackgroundService's own
    // StartAsync only kicks ExecuteAsync off as a background task, it doesn't wait for it.
    public async Task SeedAllAsync(CancellationToken ct = default)
    {
        logger.LogInformation(
            "RegulatoryNumberSequenceBackfillService: seeding {Count} counter pools",
            SeedValues.Count
        );

        foreach (var (key, value) in SeedValues)
        {
            await counters.SeedIfHigherAsync(key, value, ct);
        }

        status.MarkComplete();
        logger.LogInformation("RegulatoryNumberSequenceBackfillService: seed complete");
    }
}
