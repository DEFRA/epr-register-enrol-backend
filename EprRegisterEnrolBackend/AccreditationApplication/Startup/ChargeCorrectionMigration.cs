using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-766: bound from Migrations:Ra766ChargeCorrection. Both flags default off, so the migration
// is a no-op in every environment until deliberately switched on.
public sealed class ChargeCorrectionOptions
{
    public const string SectionName = "Migrations:Ra766ChargeCorrection";

    // Off: the migration does nothing at all (not even a read).
    public bool Enabled { get; set; }

    // Off (with Enabled on): dry run - logs what would change, writes nothing. There is no
    // SpotCheckConfirmedBy: the approved cdp-app-config pull request that sets this is the
    // sign-off, reviewed against that environment's dry run.
    public bool Apply { get; set; }
}

// RA-766: before this fix, answering a query that changed the tonnage band or the overseas sites
// made this service recalculate the charge and send it to the Case Management service, which
// dropped it, so the duly-making page kept showing the original charge. This one-off corrects the
// work items already affected. This service is the only place the charge is calculated
// (AccreditationChargeCalculator); the Case Management service only stores it.
//
// For every application linked to a work item and not yet Approved, Rejected or Withdrawn:
//   - no planned tonnage band                  -> skipped-no-band (no charge can be calculated)
//   - work item already closed there           -> skipped-terminal (see ClosedWorkItemStates)
//   - work item already holds the charge       -> unchanged
//   - otherwise: dry run logs would-update; apply sets it through the Case Management service's
//     temporary PUT .../charge and logs updated.
// Duly-made work items are corrected too. One failure is logged (the HTTP status or exception
// type only) and the run carries on. A re-run changes nothing, because corrected work items are
// then unchanged.
//
// Applications are read the way the resume adapter reads them (the legacy collection), not from
// the domain model, because DualWrite is off in prod. Nothing is written to this service's own
// data, so DomainModel:DualWrite is irrelevant here.
//
// Logging is built to be queried in Grafana (OpenSearch, Lucene). CDP only indexes the message
// text, so every value is in the message, as `key=value`. No personal data is logged: the
// application reference, work item id, pence and a duly-made flag only.
//
// A BackgroundService like AccreditationReferenceCorrectionMigration, so a failure here logs
// rather than crash-looping the host. The persistence and adapter are resolved only once the
// migration is enabled, so a disabled migration constructs nothing during host startup.
//
// Temporary: removed, with the Case Management service's PUT .../charge endpoint and the adapter
// methods that call it, once the correction is verified.
public class ChargeCorrectionMigration(
    IServiceProvider serviceProvider,
    IOptions<ChargeCorrectionOptions> options,
    ILogger<ChargeCorrectionMigration> logger
) : BackgroundService
{
    internal const string Approved = "approved";
    internal const string Rejected = "rejected";
    internal const string Withdrawn = "withdrawn";
    internal const string ApprovedPendingContact = "approved-pending-contact";
    internal const string RejectedPendingContact = "rejected-pending-contact";

    // The Case Management service states a charge is never changed in. The two pending-contact
    // states are not terminal there, but are already a decided outcome (this service shows them
    // as AwaitingDecision, so the application status alone would not catch them).
    internal static readonly IReadOnlySet<string> ClosedWorkItemStates = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        Approved,
        Rejected,
        Withdrawn,
        ApprovedPendingContact,
        RejectedPendingContact,
    };

    internal enum RunMode
    {
        Disabled,
        DryRun,
        Apply,
    }

    internal sealed record Summary(
        RunMode Mode,
        int Candidates,
        int Updated,
        int WouldUpdate,
        int Unchanged,
        int SkippedNoBand,
        int SkippedTerminal,
        int Failed
    );

    private enum Outcome
    {
        Updated,
        WouldUpdate,
        Unchanged,
        SkippedNoBand,
        SkippedTerminal,
        Failed,
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is never held up by anything below.
        await Task.Yield();
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Each application is handled on its own, so reaching here means the candidates could
            // not be read. Nothing was written; the next run starts again.
            logger.LogError(
                "RA-766 charge correction failed before any application was processed: error={Error}",
                ex.GetType().Name
            );
        }
    }

    // Extracted so tests can await the run deterministically - BackgroundService's StartAsync only
    // kicks ExecuteAsync off as a background task.
    internal async Task<Summary> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
            return new Summary(RunMode.Disabled, 0, 0, 0, 0, 0, 0, 0);

        var mode = settings.Apply ? RunMode.Apply : RunMode.DryRun;
        var modeText = mode == RunMode.Apply ? "apply" : "dry-run";

        var persistence =
            serviceProvider.GetRequiredService<IAccreditationApplicationPersistence>();
        var caseWorking = serviceProvider.GetRequiredService<ICaseWorkingApiAdapter>();

        var applications = await persistence.GetOpenWithCaseManagementWorkItemAsync(
            cancellationToken
        );

        logger.LogInformation(
            "RA-766 charge correction starting: mode={Mode} candidates={Candidates}",
            modeText,
            applications.Count
        );

        var counts = new Dictionary<Outcome, int>();
        foreach (var application in applications)
        {
            var outcome = await ProcessAsync(
                caseWorking,
                application,
                mode == RunMode.Apply,
                cancellationToken
            );
            counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
        }

        var summary = new Summary(
            mode,
            applications.Count,
            counts.GetValueOrDefault(Outcome.Updated),
            counts.GetValueOrDefault(Outcome.WouldUpdate),
            counts.GetValueOrDefault(Outcome.Unchanged),
            counts.GetValueOrDefault(Outcome.SkippedNoBand),
            counts.GetValueOrDefault(Outcome.SkippedTerminal),
            counts.GetValueOrDefault(Outcome.Failed)
        );

        logger.LogInformation(
            "RA-766 charge correction summary: mode={Mode} candidates={Candidates} updated={Updated} "
                + "would-update={WouldUpdate} unchanged={Unchanged} skipped-no-band={SkippedNoBand} "
                + "skipped-terminal={SkippedTerminal} failed={Failed}",
            modeText,
            summary.Candidates,
            summary.Updated,
            summary.WouldUpdate,
            summary.Unchanged,
            summary.SkippedNoBand,
            summary.SkippedTerminal,
            summary.Failed
        );
        return summary;
    }

    private async Task<Outcome> ProcessAsync(
        ICaseWorkingApiAdapter caseWorking,
        AccreditationApplicationModel application,
        bool apply,
        CancellationToken cancellationToken
    )
    {
        // Never null: the persistence query only returns applications with a work item.
        var workItemId = application.CaseManagementWorkItemId!.Value;

        var charge = AccreditationChargeCalculator.CalculateChargePence(application);
        if (charge is not { } newPence)
        {
            LogOutcome(Outcome.SkippedNoBand, application, workItemId, null, null, null);
            return Outcome.SkippedNoBand;
        }

        try
        {
            var workItem = await caseWorking.GetWorkItemAsync(workItemId, cancellationToken);

            if (
                workItem.StateId is { } stateId
                && ClosedWorkItemStates.Contains(stateId)
            )
            {
                LogOutcome(
                    Outcome.SkippedTerminal,
                    application,
                    workItemId,
                    workItem.ChargeAmountPence,
                    newPence,
                    workItem.DulyMade
                );
                return Outcome.SkippedTerminal;
            }

            var outcome = Outcome.Unchanged;
            if (workItem.ChargeAmountPence != newPence)
            {
                outcome = apply
                    ? ToOutcome(
                        await caseWorking.SetChargeAsync(workItemId, newPence, cancellationToken)
                    )
                    : Outcome.WouldUpdate;
            }

            LogOutcome(
                outcome,
                application,
                workItemId,
                workItem.ChargeAmountPence,
                newPence,
                workItem.DulyMade
            );
            return outcome;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The status or the exception type only: a message or body could carry personal data.
            logger.LogError(
                "RA-766 charge correction: outcome=failed applicationReference={ApplicationReference} "
                    + "workItemId={WorkItemId} error={Error} status={Status}",
                application.ApplicationReference ?? "none",
                workItemId,
                ex.GetType().Name,
                ex is HttpRequestException { StatusCode: { } status }
                    ? ((int)status).ToString(CultureInfo.InvariantCulture)
                    : "none"
            );
            return Outcome.Failed;
        }
    }

    // Closed here means the work item was closed between the read and the write.
    private static Outcome ToOutcome(SetChargeOutcome outcome) =>
        outcome switch
        {
            SetChargeOutcome.Updated => Outcome.Updated,
            SetChargeOutcome.Closed => Outcome.SkippedTerminal,
            _ => Outcome.Unchanged,
        };

    private void LogOutcome(
        Outcome outcome,
        AccreditationApplicationModel application,
        Guid workItemId,
        int? oldPence,
        int? newPence,
        bool? dulyMade
    ) =>
        logger.LogInformation(
            "RA-766 charge correction: outcome={Outcome} applicationReference={ApplicationReference} "
                + "workItemId={WorkItemId} oldPence={OldPence} newPence={NewPence} dulyMade={DulyMade}",
            OutcomeText(outcome),
            application.ApplicationReference ?? "none",
            workItemId,
            oldPence?.ToString(CultureInfo.InvariantCulture) ?? "none",
            newPence?.ToString(CultureInfo.InvariantCulture) ?? "none",
            dulyMade is { } made ? (made ? "true" : "false") : "unknown"
        );

    private static string OutcomeText(Outcome outcome) =>
        outcome switch
        {
            Outcome.Updated => "updated",
            Outcome.WouldUpdate => "would-update",
            Outcome.Unchanged => "unchanged",
            Outcome.SkippedNoBand => "skipped-no-band",
            Outcome.SkippedTerminal => "skipped-terminal",
            _ => "failed",
        };
}
