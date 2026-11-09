using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.DomainModel.Persistence;

namespace EprRegisterEnrolBackend.DomainModel.Reconciliation;

/// <summary>
/// Logs the reconciliation report at startup and every few hours, so a run of clean reports can be seen over days
/// (phase 4). CDP gives no shell, so logs are how a test environment reports back. It never runs in live, whatever
/// the flag says.
/// </summary>
public sealed class DomainModelReconciliationService(
    IServiceProvider services,
    IDomainModelSchemaStatus schema,
    TimeProvider clock,
    ILogger<DomainModelReconciliationService> logger
) : BackgroundService
{
    internal Func<string?> CdpEnvironment { get; init; } =
        TestOrganisationsConfig.ReadCdpEnvironment;
    internal TimeSpan Interval { get; init; } = TimeSpan.FromHours(6);
    internal TimeSpan WaitDelay { get; init; } = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is never held up by anything below.
        await Task.Yield();
        if (IsLive())
        {
            logger.LogWarning("DomainModelReconciliation is off: it never runs in live");
            return;
        }

        try
        {
            while (true)
            {
                await ReportOnceAsync(stoppingToken);
                await Task.Delay(Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
    }

    private bool IsLive() =>
        string.Equals(
            CdpEnvironment()?.Trim(),
            TestOrganisationsConfig.ProductionCdpEnvironment,
            StringComparison.OrdinalIgnoreCase
        );

    // Never lets an exception out: an unhandled one in a background service stops the whole backend. A failed report is
    // logged and tried again at the next interval.
    private async Task ReportOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await schema.WaitUntilBuiltAsync(WaitDelay, clock, cancellationToken);
            // Resolved only now: constructing these builds indexes, which must not hold up startup.
            var reconciler = services.GetRequiredService<ApplicationReconciler>();
            Log(await reconciler.RunAsync(cancellationToken));
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested
            )
        {
            LogFailure(ex);
        }
    }

    // The error kind only: a driver's message can quote document values, and logs must hold no personal data.
    private void LogFailure(Exception ex) =>
        logger.LogError(
            "DomainModelReconciliation failed: error {ErrorKind}",
            MongoErrors.Kind(ex)
        );

    private void Log(ReconciliationReport report)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("DomainModelReconciliation report {Report}", report.ToLogLine());
        }

        foreach (var (kind, applications) in report.UnreadableByKind)
        {
            logger.LogWarning(
                "DomainModelReconciliation unreadable: error {ErrorKind}, applications {Applications}",
                kind,
                applications
            );
        }

        foreach (var (field, applications) in report.DifferencesByField)
        {
            logger.LogWarning(
                "DomainModelReconciliation difference: field {Field}, applications {Applications}",
                field,
                applications
            );
        }

        foreach (var (field, applications) in report.ModelOnlyByField)
        {
            logger.LogWarning(
                "DomainModelReconciliation value only in the model, no legacy value to explain it: field {Field}, applications {Applications}",
                field,
                applications
            );
        }

        if (report.SampleApplicationIds.Count > 0 && logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning(
                "DomainModelReconciliation example applications: {ApplicationIds}",
                string.Join(",", report.SampleApplicationIds)
            );
        }
    }
}
