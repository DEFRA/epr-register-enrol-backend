using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-691: bound from Migrations:Ra691AccreditationReferenceCorrection. Every flag defaults off,
// so the migration is a no-op in every environment until deliberately switched on.
public sealed class AccreditationReferenceCorrectionOptions
{
    public const string SectionName = "Migrations:Ra691AccreditationReferenceCorrection";

    // Off: the migration does nothing at all (not even a read).
    public bool Enabled { get; set; }

    // Off (with Enabled on): dry run - logs each planned "org: old -> new" change, writes nothing.
    public bool Apply { get; set; }

    // Who reviewed the dry-run output against the live records. Required before Apply takes
    // effect; logged with every applied change so the authorisation outlives the env var.
    public string? SpotCheckConfirmedBy { get; set; }
}

// RA-691: corrects the accreditation references of two approved 2027 renewals that were issued a
// fresh counter sequence instead of keeping the sequence from their 2026 reference (root cause
// RA-690). One-off and scoped to exactly these two records - there is deliberately no query that
// could widen it.
//
// Matching rule: each target is located by the reference it currently holds - incorrect or
// corrected - and only then checked against its stable identity (OrgId, Year 2027, Plastic,
// reprocessor). The reference is the only key that singles out one accreditation: an
// organisation can hold several 2027 plastic reprocessor accreditations (one per site), so the
// identity fields alone cannot be asserted unique. For each target:
//   - incorrect held by exactly one matching record, corrected by none      -> fix
//   - corrected held by exactly one matching record, incorrect by none      -> skip (already done)
//   - neither value held by any record                                     -> abort
//   - corrected value held while the incorrect one still is (another record) -> abort
//   - either value held by more than one record, or by a record whose
//     identity does not match                                              -> abort
// Every target is planned before anything is written, so an abort on either one means no writes
// at all.
//
// Each fix is a single targeted update guarded on the record still holding the incorrect value,
// so a concurrent write cannot be clobbered, and the incorrect value goes onto
// PreviousAccreditationNumbers via $addToSet (the RA-448 regenerate audit trail) so it is never
// duplicated. DateLastEdited is deliberately left alone: AccreditationApiMapper falls back to it
// as the approval date reported to service clients.
//
// A BackgroundService like RegulatoryNumberSequenceBackfillService, so a Mongo failure here logs
// rather than crash-looping the host. The persistence is resolved only once the migration is
// enabled, so a disabled migration never constructs (and index-builds) it during host startup -
// MongoIndexInitializerService owns that.
public class AccreditationReferenceCorrectionMigration(
    IServiceProvider serviceProvider,
    IOptions<AccreditationReferenceCorrectionOptions> options,
    ILogger<AccreditationReferenceCorrectionMigration> logger
) : BackgroundService
{
    internal sealed record Correction(
        string OrganisationName,
        int OrgId,
        string Incorrect,
        string Corrected
    );

    internal const int AccreditationYear = 2027;

    internal static readonly IReadOnlyList<Correction> Corrections =
    [
        new("Infinity Plastic Solutions Ltd", 500527, "A27ER5005270322PL", "A27ER5005270246PL"),
        new("PLASGRAN LIMITED", 500105, "A27ER5001050321PL", "A27ER5001050197PL"),
    ];

    internal enum RunResult
    {
        Disabled,
        DryRun,
        Applied,
        Aborted,
    }

    private sealed record Plan(Correction Correction, ObjectId ApplicationId, bool AlreadyCorrect);

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
            // Every plan check runs before any write, and each write is independently guarded,
            // so a failure part-way leaves a state the next run re-plans from safely.
            logger.LogError(ex, "RA-691 accreditation reference correction failed");
        }
    }

    // Extracted so tests can await the run deterministically - BackgroundService's StartAsync only
    // kicks ExecuteAsync off as a background task.
    internal async Task<RunResult> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
            return RunResult.Disabled;

        var apply = settings.Apply;
        if (apply && string.IsNullOrWhiteSpace(settings.SpotCheckConfirmedBy))
        {
            logger.LogError(
                "RA-691 accreditation reference correction: Apply is set but SpotCheckConfirmedBy "
                    + "is not, so running as a DRY RUN. Set it to whoever reviewed the dry-run "
                    + "output against the live records."
            );
            apply = false;
        }

        var mode = apply ? "APPLY" : "DRY RUN (nothing will be written)";
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-691 accreditation reference correction starting. Mode={Mode}",
                mode
            );

        var persistence = serviceProvider.GetRequiredService<IAccreditationApplicationPersistence>();

        var plans = new List<Plan>();
        foreach (var correction in Corrections)
        {
            var plan = await PlanAsync(persistence, correction, cancellationToken);
            if (plan is null)
            {
                logger.LogError(
                    "RA-691 accreditation reference correction aborted: nothing has been written"
                );
                return RunResult.Aborted;
            }
            plans.Add(plan);
        }

        foreach (var plan in plans)
            await ReportAndApplyAsync(
                persistence,
                plan,
                mode,
                apply ? settings.SpotCheckConfirmedBy : null,
                cancellationToken
            );

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-691 accreditation reference correction complete. Mode={Mode}",
                mode
            );
        return apply ? RunResult.Applied : RunResult.DryRun;
    }

    // Logs one planned change (or skip) and, when confirmedBy is set (an authorised apply run),
    // writes it.
    private async Task ReportAndApplyAsync(
        IAccreditationApplicationPersistence persistence,
        Plan plan,
        string mode,
        string? confirmedBy,
        CancellationToken cancellationToken
    )
    {
        var c = plan.Correction;
        if (plan.AlreadyCorrect)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "RA-691 {Mode}: {OrganisationName} ({OrgId}) application {ApplicationId} "
                        + "already holds {Corrected}; skipped",
                    mode,
                    c.OrganisationName,
                    c.OrgId,
                    plan.ApplicationId,
                    c.Corrected
                );
            }
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "RA-691 {Mode}: {OrganisationName} ({OrgId}) application {ApplicationId}: "
                    + "{Incorrect} -> {Corrected}",
                mode,
                c.OrganisationName,
                c.OrgId,
                plan.ApplicationId,
                c.Incorrect,
                c.Corrected
            );
        }

        if (confirmedBy is not null)
            await ApplyAsync(persistence, plan, confirmedBy, cancellationToken);
    }

    private async Task<Plan?> PlanAsync(
        IAccreditationApplicationPersistence persistence,
        Correction c,
        CancellationToken cancellationToken
    )
    {
        var holdingIncorrect = await persistence.GetByAccreditationReferenceAsync(
            c.Incorrect,
            cancellationToken
        );
        var holdingCorrected = await persistence.GetByAccreditationReferenceAsync(
            c.Corrected,
            cancellationToken
        );

        if (holdingIncorrect.Count > 1 || holdingCorrected.Count > 1)
        {
            logger.LogError(
                "RA-691 {OrganisationName} ({OrgId}): {Incorrect} is held by {IncorrectCount} "
                    + "records and {Corrected} by {CorrectedCount}; expected at most one each",
                c.OrganisationName,
                c.OrgId,
                c.Incorrect,
                holdingIncorrect.Count,
                c.Corrected,
                holdingCorrected.Count
            );
            return null;
        }

        if (holdingIncorrect.Count == 1 && holdingCorrected.Count == 1)
        {
            logger.LogError(
                "RA-691 {OrganisationName} ({OrgId}): corrected value {Corrected} is already held "
                    + "by application {OtherApplicationId} while application {ApplicationId} "
                    + "still holds {Incorrect}",
                c.OrganisationName,
                c.OrgId,
                c.Corrected,
                holdingCorrected[0].Id,
                holdingIncorrect[0].Id,
                c.Incorrect
            );
            return null;
        }

        if (holdingIncorrect.Count == 0 && holdingCorrected.Count == 0)
        {
            logger.LogError(
                "RA-691 {OrganisationName} ({OrgId}): no record holds {Incorrect} or {Corrected}; "
                    + "its current reference matches neither",
                c.OrganisationName,
                c.OrgId,
                c.Incorrect,
                c.Corrected
            );
            return null;
        }

        var alreadyCorrect = holdingIncorrect.Count == 0;
        var target = alreadyCorrect ? holdingCorrected[0] : holdingIncorrect[0];
        if (!IsTargetAccreditation(target, c) || target.Id is not { } applicationId)
        {
            logger.LogError(
                "RA-691 {OrganisationName} ({OrgId}): application {ApplicationId} holding "
                    + "{Reference} is OrgId {ActualOrgId}, Year {Year}, {MaterialType}, "
                    + "IsExporter {IsExporter}; expected OrgId {ExpectedOrgId}, Year {ExpectedYear}, "
                    + "Plastic, reprocessor",
                c.OrganisationName,
                c.OrgId,
                target.Id,
                target.AccreditationReference,
                target.OrgId,
                target.Year,
                target.MaterialType,
                target.IsExporter,
                c.OrgId,
                AccreditationYear
            );
            return null;
        }

        return new Plan(c, applicationId, alreadyCorrect);
    }

    // The ER...PL references are England, reprocessor, plastic - the nation is not checked
    // because Nation is nullable on applications seeded before RA-526.
    private static bool IsTargetAccreditation(AccreditationApplicationModel application, Correction c) =>
        application.OrgId == c.OrgId
        && application.Year == AccreditationYear
        && application.MaterialType == MaterialType.Plastic
        && !application.IsExporter;

    private async Task ApplyAsync(
        IAccreditationApplicationPersistence persistence,
        Plan plan,
        string confirmedBy,
        CancellationToken cancellationToken
    )
    {
        var c = plan.Correction;
        var updated = await persistence.UpdateFieldsAsync(
            plan.ApplicationId,
            Builders<AccreditationApplicationModel>.Filter.Eq(
                a => a.AccreditationReference,
                c.Incorrect
            ),
            Builders<AccreditationApplicationModel>
                .Update.Set(a => a.AccreditationReference, c.Corrected)
                .AddToSet(a => a.PreviousAccreditationNumbers, c.Incorrect),
            cancellationToken
        );

        if (updated is null)
        {
            logger.LogError(
                "RA-691 {OrganisationName} ({OrgId}) application {ApplicationId} no longer holds "
                    + "{Incorrect}; it changed after planning, so nothing was written to it. "
                    + "Re-run the dry run to see its current state.",
                c.OrganisationName,
                c.OrgId,
                plan.ApplicationId,
                c.Incorrect
            );
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "RA-691 APPLIED: {OrganisationName} ({OrgId}) application {ApplicationId}: "
                    + "{Incorrect} -> {Corrected}. Spot check confirmed by {ConfirmedBy}",
                c.OrganisationName,
                c.OrgId,
                plan.ApplicationId,
                c.Incorrect,
                c.Corrected,
                confirmedBy
            );
        }
    }
}
