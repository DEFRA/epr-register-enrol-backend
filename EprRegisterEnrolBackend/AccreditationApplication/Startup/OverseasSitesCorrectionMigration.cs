using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-768: bound from Migrations:Ra768OverseasSitesCorrection. Every flag defaults off, so the
// migration is a no-op in every environment until deliberately switched on.
public sealed class OverseasSitesCorrectionOptions
{
    public const string SectionName = "Migrations:Ra768OverseasSitesCorrection";

    // Off: the migration does nothing at all (not even a read).
    public bool Enabled { get; set; }

    // Off (with Enabled on): dry run - logs what would be removed, calls nothing that writes.
    public bool Apply { get; set; }

    // Who reviewed the dry-run output against the live records. Required before Apply takes
    // effect. Not logged (it is a person's name, and this migration logs no personal data, as
    // RA-788's does not); the authorisation is recorded in cdp-app-config's history.
    public string? SpotCheckConfirmedBy { get; set; }
}

// RA-768: before RA-557 (management-be 64efbca, 11 September 2026), when an operator answered a
// query on the overseas sites and deselected or reverted a site, the Case Management service saved
// the resubmitted list only in payload.latestSections, so payload.overseasSites.sites (what its
// pages read) kept the site. RA-557 fixed new resubmissions; this one-off corrects the work items
// already affected, through the Case Management service's POST .../overseas-sites/remove, which
// removes named sites only and never replaces the list (a regulator may have changed operation
// codes on the sites that stay).
//
// Every application linked to a work item is a candidate:
//   - Queried in this service                   -> skipped-queried (not read: the operator can
//     still change the selection, and resubmitting syncs it through RA-557; removing a site first
//     would show the regulator a change the operator has not submitted)
//   - work item unreadable, its overseasSites missing or malformed, or a site with neither id
//                                               -> failed
//   - the sites to remove are the ones the work item holds that this service holds with
//     Selected = false, matched by orsId, or by siteId only when the work item's site has no orsId
//     (the Case Management endpoint's own rule). A work item site this service does not hold at
//     all is not removed: it is logged as unmatched, for analysis.
//   - nothing to remove                         -> unchanged
//   - Approved, Rejected or Withdrawn here      -> skipped-decided (list only: the work item is
//     read, never written)
//   - work item state missing                   -> failed
//   - work item decided (CaseWorkingWorkItem.DecidedStates; checked before duly made, as the
//     Case Management service does)            -> skipped-decided
//   - work item duly made (payload.paymentDate) -> skipped-duly-made
//   - audit log missing or unreadable           -> failed
//   - cause not confirmed: no query-responded audit entry before CauseCutoff whose section keys
//     include the overseas sites or BES evidence section (RA-557 merges both into
//     payload.overseasSites)                   -> skipped-cause-unconfirmed (list only)
//   - otherwise: dry run logs would-update; apply calls the endpoint and logs updated, or
//     unchanged when every site was already gone, or the skipped outcome when it refused with a
//     409 (decided or duly-made).
// The skipped-decided, skipped-duly-made and skipped-cause-unconfirmed lines are the lists for the
// business owner: they carry the site ids that would have been removed. One failure is logged at
// Error (the HTTP status, exception type or the unreadable value only) and the run carries on. A
// re-run changes nothing, because corrected work items are then unchanged.
//
// Applications are read from the legacy collection, not the domain model, because DualWrite is
// off in prod. Nothing is written to this service's own data.
//
// Logging is built to be queried in Grafana (OpenSearch, Lucene). CDP only indexes the message
// text, so every value is in the message, as `key=value`. No personal data is logged: the
// application reference, work item id, site ids (orsId/siteId), statuses and outcome only - never
// a site name, address or contact.
//
// Run it before RA-766's ChargeCorrectionMigration, or re-check the charges afterwards: the
// endpoint removes sites only and never changes the charge the work item holds, so the charge is
// checked against the corrected site list.
//
// A BackgroundService like AccreditationReferenceCorrectionMigration, so a failure here logs rather
// than crash-looping the host. The persistence and adapter are resolved only once the migration is
// enabled, so a disabled migration constructs nothing during host startup.
//
// Temporary: removed, with the adapter's RemoveOverseasSitesAsync and the Case Management
// service's endpoint, once the correction is verified in prod.
public class OverseasSitesCorrectionMigration(
    IServiceProvider serviceProvider,
    IOptions<OverseasSitesCorrectionOptions> options,
    ILogger<OverseasSitesCorrectionMigration> logger
) : BackgroundService
{
    // Stored by the Case Management service on its overseas-sites-corrected audit entry. No
    // personal data.
    internal const string Reason =
        "RA-768: removing overseas sites the operator deselected in a query response before RA-557";

    // RA-557 was merged on 11 September 2026 (ticket background). Only a query response the Case
    // Management service recorded before then fits the defect this story fixes (ticket Scope
    // comment: "a query-responded entry before 11 September 2026 whose sectionKeys include overseas
    // sites"). Anything else is listed, not fixed.
    internal static readonly DateTime CauseCutoff = new(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);

    // The section keys whose resubmission RA-557 merges into payload.overseasSites.
    internal static readonly IReadOnlySet<string> SiteSectionKeys = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        AccreditationApplicationSections.OverseasReprocessingSitesKey,
        AccreditationApplicationSections.BroadlyEquivalentStandardsKey,
    };

    private static readonly ApplicationStatus[] ClosedStatuses =
    [
        ApplicationStatus.Approved,
        ApplicationStatus.Rejected,
        ApplicationStatus.Withdrawn,
    ];

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
        int SkippedQueried,
        int SkippedDulyMade,
        int SkippedDecided,
        int SkippedCauseUnconfirmed,
        int Failed,
        int UnmatchedSites
    );

    internal enum Outcome
    {
        Updated,
        WouldUpdate,
        Unchanged,
        SkippedQueried,
        SkippedDulyMade,
        SkippedDecided,
        SkippedCauseUnconfirmed,
        Failed,
    }

    private sealed record Result(Outcome Outcome, int UnmatchedSites);

    // The planned outcome for an open application with sites to remove, or the unreadable value
    // that makes it a failure.
    private sealed record Plan(Outcome Outcome, string? Error = null);

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
                "RA-768 overseas sites correction failed before any application was processed: error={Error}",
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
            return new Summary(RunMode.Disabled, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        var apply = settings.Apply;
        if (apply && string.IsNullOrWhiteSpace(settings.SpotCheckConfirmedBy))
        {
            logger.LogError(
                "RA-768 overseas sites correction: Apply is set but SpotCheckConfirmedBy is not, so "
                    + "running as a DRY RUN. Set it to whoever reviewed the dry-run output against "
                    + "the live records."
            );
            apply = false;
        }

        var mode = apply ? RunMode.Apply : RunMode.DryRun;
        var modeText = apply ? "apply" : "dry-run";

        var persistence =
            serviceProvider.GetRequiredService<IAccreditationApplicationPersistence>();
        var caseWorking = serviceProvider.GetRequiredService<ICaseWorkingApiAdapter>();

        var applications = await persistence.GetWithCaseManagementWorkItemAsync(
            cancellationToken
        );

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-768 overseas sites correction starting: mode={Mode} candidates={Candidates}",
                modeText,
                applications.Count
            );

        var counts = new Dictionary<Outcome, int>();
        var unmatched = 0;
        foreach (var application in applications)
        {
            var result = await ProcessAsync(caseWorking, application, apply, cancellationToken);
            counts[result.Outcome] = counts.GetValueOrDefault(result.Outcome) + 1;
            unmatched += result.UnmatchedSites;
        }

        var summary = new Summary(
            mode,
            applications.Count,
            counts.GetValueOrDefault(Outcome.Updated),
            counts.GetValueOrDefault(Outcome.WouldUpdate),
            counts.GetValueOrDefault(Outcome.Unchanged),
            counts.GetValueOrDefault(Outcome.SkippedQueried),
            counts.GetValueOrDefault(Outcome.SkippedDulyMade),
            counts.GetValueOrDefault(Outcome.SkippedDecided),
            counts.GetValueOrDefault(Outcome.SkippedCauseUnconfirmed),
            counts.GetValueOrDefault(Outcome.Failed),
            unmatched
        );

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-768 overseas sites correction summary: mode={Mode} candidates={Candidates} "
                    + "updated={Updated} would-update={WouldUpdate} unchanged={Unchanged} "
                    + "skipped-queried={SkippedQueried} skipped-duly-made={SkippedDulyMade} "
                    + "skipped-decided={SkippedDecided} "
                    + "skipped-cause-unconfirmed={SkippedCauseUnconfirmed} failed={Failed} "
                    + "unmatched-sites={UnmatchedSites}",
                modeText,
                summary.Candidates,
                summary.Updated,
                summary.WouldUpdate,
                summary.Unchanged,
                summary.SkippedQueried,
                summary.SkippedDulyMade,
                summary.SkippedDecided,
                summary.SkippedCauseUnconfirmed,
                summary.Failed,
                summary.UnmatchedSites
            );
        return summary;
    }

    private async Task<Result> ProcessAsync(
        ICaseWorkingApiAdapter caseWorking,
        AccreditationApplicationModel application,
        bool apply,
        CancellationToken cancellationToken
    )
    {
        // Never null: the persistence query only returns applications with a work item.
        var workItemId = application.CaseManagementWorkItemId!.Value;

        if (application.ApplicationStatus == ApplicationStatus.Queried)
        {
            LogOutcome(Outcome.SkippedQueried, application, workItemId, null, []);
            return new Result(Outcome.SkippedQueried, 0);
        }

        // Counted even if the run fails later on, as their lines are already logged.
        var unmatchedCount = 0;
        try
        {
            var workItem = await caseWorking.GetWorkItemAsync(
                workItemId,
                CaseWorkingCorrectionActor.OverseasSitesCorrection,
                cancellationToken
            );

            if (workItem.OverseasSites is not { } held)
                return Failed(application, workItemId, "overseas-sites-unreadable");
            if (held.Any(s => s.OrsId is null && s.SiteId is null))
                return Failed(application, workItemId, "site-without-id");

            var (toRemove, unmatched) = Classify(application, held);
            foreach (var site in unmatched)
                LogUnmatched(application, workItemId, site);
            unmatchedCount = unmatched.Count;

            if (toRemove.Count == 0)
            {
                LogOutcome(Outcome.Unchanged, application, workItemId, workItem, []);
                return new Result(Outcome.Unchanged, unmatchedCount);
            }

            // Closed here: decided, whatever the work item says. Listed, never written.
            var plan = ClosedStatuses.Contains(application.ApplicationStatus)
                ? new Plan(Outcome.SkippedDecided)
                : PlanFor(workItem, apply);
            if (plan.Error is { } error)
                return Failed(application, workItemId, error, unmatchedCount);

            var (outcome, sites) = await CorrectAsync(
                caseWorking,
                workItemId,
                plan.Outcome,
                toRemove,
                cancellationToken
            );

            LogOutcome(outcome, application, workItemId, workItem, sites);
            return new Result(outcome, unmatchedCount);
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The status or the exception type only: a message or body could carry personal data.
            logger.LogError(
                "RA-768 overseas sites correction: outcome=failed applicationReference={ApplicationReference} "
                    + "workItemId={WorkItemId} error={Error} status={Status}",
                application.ApplicationReference ?? "none",
                workItemId,
                ex.GetType().Name,
                ex is HttpRequestException { StatusCode: { } status }
                    ? ((int)status).ToString(CultureInfo.InvariantCulture)
                    : "none"
            );
            return new Result(Outcome.Failed, unmatchedCount);
        }
    }

    // For an open application with sites to remove. Decided is checked before duly made, as the
    // Case Management service does; both before the cause, so the business owner's list of
    // untouched work items is complete whatever the cause.
    private static Plan PlanFor(CaseWorkingWorkItem workItem, bool apply)
    {
        if (workItem.StateId is null)
            return new Plan(Outcome.Failed, "state-missing");
        if (workItem.IsDecided)
            return new Plan(Outcome.SkippedDecided);
        if (workItem.DulyMade)
            return new Plan(Outcome.SkippedDulyMade);
        if (workItem.QueryResponses is not { } responses)
            return new Plan(Outcome.Failed, "audit-log-unreadable");
        if (!CauseConfirmed(responses))
            return new Plan(Outcome.SkippedCauseUnconfirmed);
        return new Plan(apply ? Outcome.Updated : Outcome.WouldUpdate);
    }

    // The defect this story fixes: a query response recorded before RA-557 that resubmitted the
    // overseas sites or the BES evidence.
    internal static bool CauseConfirmed(IReadOnlyList<CaseWorkingQueryResponse> responses) =>
        responses.Any(r =>
            r.CreatedAt < CauseCutoff && r.SectionKeys.Any(SiteSectionKeys.Contains)
        );

    // Calls the endpoint only for a planned update; otherwise the plan stands, with the sites that
    // would have been removed. Returns the outcome and the site ids to log.
    private static async Task<(
        Outcome Outcome,
        IReadOnlyList<CaseWorkingSiteIds> Sites
    )> CorrectAsync(
        ICaseWorkingApiAdapter caseWorking,
        Guid workItemId,
        Outcome planned,
        List<CaseWorkingSiteIds> toRemove,
        CancellationToken cancellationToken
    )
    {
        if (planned != Outcome.Updated)
            return (planned, toRemove);

        var result = await caseWorking.RemoveOverseasSitesAsync(
            workItemId,
            toRemove,
            Reason,
            cancellationToken
        );
        return result.Outcome switch
        {
            RemoveOverseasSitesOutcome.Updated => (Outcome.Updated, result.Removed),
            RemoveOverseasSitesOutcome.Unchanged => (Outcome.Unchanged, result.Removed),
            RemoveOverseasSitesOutcome.Decided => (Outcome.SkippedDecided, toRemove),
            RemoveOverseasSitesOutcome.DulyMade => (Outcome.SkippedDulyMade, toRemove),
            _ => throw new InvalidOperationException(
                $"Unhandled remove outcome {result.Outcome}"
            ),
        };
    }

    // Splits the sites the work item holds into those to remove (every matching site here is
    // deselected) and those this service does not hold at all. A site matched here and selected
    // stays.
    internal static (
        List<CaseWorkingSiteIds> ToRemove,
        List<CaseWorkingSiteIds> Unmatched
    ) Classify(AccreditationApplicationModel application, IReadOnlyList<CaseWorkingSiteIds> held)
    {
        // No overseas sites section here means the application holds no sites (a reprocessor's
        // never has one), so every site the work item holds is unmatched.
        var ours = application.OverseasSites?.Sites ?? [];
        var toRemove = new List<CaseWorkingSiteIds>();
        var unmatched = new List<CaseWorkingSiteIds>();
        foreach (var site in held)
        {
            var matches = ours.Where(o => Matches(o, site)).ToList();
            if (matches.Count == 0)
                unmatched.Add(site);
            else if (matches.TrueForAll(o => !o.Selected))
                toRemove.Add(site);
        }
        return (toRemove, unmatched);
    }

    private static bool Matches(OverseasSiteModel ours, CaseWorkingSiteIds held) =>
        held.OrsId is { } orsId
            ? string.Equals(ours.OrsId, orsId, StringComparison.Ordinal)
            : string.Equals(
                ours.SiteId.ToString(CultureInfo.InvariantCulture),
                held.SiteId,
                StringComparison.Ordinal
            );

    private Result Failed(
        AccreditationApplicationModel application,
        Guid workItemId,
        string error,
        int unmatchedSites = 0
    )
    {
        logger.LogError(
            "RA-768 overseas sites correction: outcome=failed applicationReference={ApplicationReference} "
                + "workItemId={WorkItemId} error={Error} status=none",
            application.ApplicationReference ?? "none",
            workItemId,
            error
        );
        return new Result(Outcome.Failed, unmatchedSites);
    }

    private void LogUnmatched(
        AccreditationApplicationModel application,
        Guid workItemId,
        CaseWorkingSiteIds site
    )
    {
        // Warning: a site the Case Management service holds that this service never had is outside
        // what this correction may fix, and needs looking at.
        logger.LogWarning(
            "RA-768 overseas sites correction: unmatched applicationReference={ApplicationReference} "
                + "workItemId={WorkItemId} site={Site}",
            application.ApplicationReference ?? "none",
            workItemId,
            SiteText(site)
        );
    }

    private void LogOutcome(
        Outcome outcome,
        AccreditationApplicationModel application,
        Guid workItemId,
        CaseWorkingWorkItem? workItem,
        IReadOnlyList<CaseWorkingSiteIds> sites
    )
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;

        var line =
            $"outcome={OutcomeText(outcome)} "
            + $"applicationReference={application.ApplicationReference ?? "none"} "
            + $"workItemId={workItemId} "
            + $"sites={(sites.Count == 0 ? "none" : string.Join(",", sites.Select(SiteText)))}";
        // Which side is closed: this service's status and the work item's state.
        if (outcome == Outcome.SkippedDecided)
            line +=
                $" applicationStatus={application.ApplicationStatus} "
                + $"workItemState={workItem?.StateId ?? "none"}";

        logger.LogInformation("RA-768 overseas sites correction: {Line}", line);
    }

    // orsId/siteId, "none" for an id the site does not have.
    private static string SiteText(CaseWorkingSiteIds site) =>
        $"{site.OrsId ?? "none"}/{site.SiteId ?? "none"}";

    internal static string OutcomeText(Outcome outcome) =>
        outcome switch
        {
            Outcome.Updated => "updated",
            Outcome.WouldUpdate => "would-update",
            Outcome.Unchanged => "unchanged",
            Outcome.SkippedQueried => "skipped-queried",
            Outcome.SkippedDulyMade => "skipped-duly-made",
            Outcome.SkippedDecided => "skipped-decided",
            Outcome.SkippedCauseUnconfirmed => "skipped-cause-unconfirmed",
            Outcome.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
}
