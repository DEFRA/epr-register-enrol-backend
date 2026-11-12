using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-764: bound from Migrations:Ra764OrganisationNameCorrection. Every flag defaults off, so the
// migration is a no-op in every environment until deliberately switched on.
public sealed class OrganisationNameCorrectionOptions
{
    public const string SectionName = "Migrations:Ra764OrganisationNameCorrection";

    // Off: the migration does nothing at all (not even a read).
    public bool Enabled { get; set; }

    // Off (with Enabled on): dry run - logs each planned "old -> new" rename, writes nothing.
    public bool Apply { get; set; }

    // Who reviewed the dry-run output against the live records. Required before Apply takes
    // effect; logged with every applied change so the authorisation outlives the env var.
    public string? SpotCheckConfirmedBy { get; set; }
}

// RA-764: organisation 500069 has been renamed in ReEx (RREPW) from "Encirc Ltd" to "VIDRALA UK &
// IRELAND LIMITED". A 2027 application keeps the copy of the name taken when it was started, so
// this one-off renames that copy on every 2027 application for 500069, and on the RA-644 domain
// model organisation when there is one. Scoped to exactly one organisation and one year - there is
// deliberately no query that could widen it.
//
// Where the name is held: the legacy application's single top-level organisationName, and the
// domain model organisation's name (the domain read model projects it from there, so nothing else
// holds a copy). Case management holds its own copy, which management-be corrects.
//
// Matching rule: the 2027 applications are found by OrgId and Year, then every one must belong to
// 500069 on every key it holds - the same ReEx organisation id, and registration and accreditation
// references that carry 500069 - and hold the old or the new name. At least one must hold the
// registration reference the story names (R26NR5000690003GL), proving it is the accreditation we
// were asked about. The domain organisation is found by the applications' ReEx organisation id or by
// number 500069; more than one, a mismatched key, or an unexpected name aborts. Every record is
// planned before anything is written, so an abort means no writes at all.
//
// Each rename is a single targeted update guarded on the record still holding the old name, so a
// re-run is a no-op and a concurrent write cannot be clobbered. Only the name is set: status,
// references and history (PreviousRegistrationNumbers, PreviousAccreditationNumbers,
// DateLastEdited) are left alone and no audit entry is written. The legacy write also bumps
// UpdatedAt and Version, as every UpdateFieldsAsync does, so a stale whole-document replace fails
// instead of writing the old name back.
//
// The domain model organisation is written alongside the legacy applications (not in one
// transaction): each write is independently guarded, so a failure part-way is repaired by the next
// run. Because both copies are written, this does not need dual-write to be off (contrast RA-691).
//
// A BackgroundService, so a Mongo failure here logs rather than crash-looping the host. The
// persistence is resolved only once the migration is enabled, so a disabled migration never
// constructs (and index-builds) it during host startup.
public class OrganisationNameCorrectionMigration(
    IServiceProvider serviceProvider,
    IOptions<OrganisationNameCorrectionOptions> options,
    ILogger<OrganisationNameCorrectionMigration> logger
) : BackgroundService
{
    internal const int OrgId = 500069;
    internal const int AccreditationYear = 2027;
    internal const string OldName = "Encirc Ltd";
    internal const string NewName = "VIDRALA UK & IRELAND LIMITED";
    internal const string RegistrationReference = "R26NR5000690003GL";

    internal enum RunResult
    {
        Disabled,
        DryRun,
        Applied,
        Aborted,
    }

    private sealed record ApplicationPlan(
        ObjectId Id,
        string? ApplicationReference,
        string? RegistrationReference,
        string? AccreditationReference,
        ApplicationStatus Status,
        bool AlreadyRenamed
    );

    private sealed record OrganisationPlan(ObjectId Id, bool AlreadyRenamed);

    private sealed record Plan(
        IReadOnlyList<ApplicationPlan> Applications,
        OrganisationPlan? Organisation
    );

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
            logger.LogError(ex, "RA-764 organisation name correction failed");
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
                "RA-764 organisation name correction: Apply is set but SpotCheckConfirmedBy is "
                    + "not, so running as a DRY RUN. Set it to whoever reviewed the dry-run output "
                    + "against the live records."
            );
            apply = false;
        }

        var mode = apply ? "APPLY" : "DRY RUN (nothing will be written)";
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-764 organisation name correction starting. Mode={Mode}",
                mode
            );

        var applications =
            serviceProvider.GetRequiredService<IAccreditationApplicationPersistence>();
        // Registered only with DomainModel:ExpandEnabled; without it there is no domain organisation.
        var organisations = serviceProvider.GetService<OrganisationPersistence>();

        var plan = await PlanAsync(applications, organisations, cancellationToken);
        if (plan is null)
        {
            logger.LogError(
                "RA-764 organisation name correction aborted: nothing has been written"
            );
            return RunResult.Aborted;
        }

        var confirmedBy = apply ? settings.SpotCheckConfirmedBy : null;
        foreach (var application in plan.Applications)
            await ReportAndApplyAsync(
                applications,
                application,
                mode,
                confirmedBy,
                cancellationToken
            );

        if (plan.Organisation is { } organisation)
            await ReportAndApplyAsync(
                organisations!,
                organisation,
                mode,
                confirmedBy,
                cancellationToken
            );
        else if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-764 {Mode}: no domain model organisation for {OrgId}; skipped",
                mode,
                OrgId
            );

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("RA-764 organisation name correction complete. Mode={Mode}", mode);
        return apply ? RunResult.Applied : RunResult.DryRun;
    }

    private async Task<Plan?> PlanAsync(
        IAccreditationApplicationPersistence applications,
        OrganisationPersistence? organisations,
        CancellationToken cancellationToken
    )
    {
        var found = await applications.GetByOrgIdAndYearAsync(
            OrgId,
            AccreditationYear,
            cancellationToken
        );
        if (found.Count == 0)
        {
            logger.LogError(
                "RA-764 {OrgId}: no {Year} application found; expected its accreditation",
                OrgId,
                AccreditationYear
            );
            return null;
        }

        var reExOrganisationIds = found.Select(a => a.OrganisationId).Distinct().ToList();
        if (reExOrganisationIds.Count != 1)
        {
            logger.LogError(
                "RA-764 {OrgId}: its {Year} applications hold {Count} different ReEx organisation "
                    + "ids ({ReExOrganisationIds}); expected one",
                OrgId,
                AccreditationYear,
                reExOrganisationIds.Count,
                string.Join(", ", reExOrganisationIds)
            );
            return null;
        }

        // OrgId is resolved on first submission (RA-503), so a 2027 application still being drafted
        // may not hold it yet: pick those up by the ReEx organisation id too.
        var byReExId = await applications.GetByOrganisationAsync(reExOrganisationIds[0]);
        var foundIds = found.Select(a => a.Id).ToHashSet();
        found =
        [
            .. found,
            .. byReExId.Where(a =>
                a.Year == AccreditationYear && a.OrgId is null && !foundIds.Contains(a.Id)
            ),
        ];

        var applicationPlans = new List<ApplicationPlan>();
        foreach (var application in found)
        {
            if (!IsTargetApplication(application) || application.Id is not { } id)
            {
                logger.LogError(
                    "RA-764 {OrgId}: application {ApplicationId} is Year {Year}, registration "
                        + "{RegistrationReference}, accreditation {AccreditationReference}, name "
                        + "\"{OrganisationName}\"; expected {ExpectedYear} references for {OrgId} "
                        + "and the name \"{OldName}\" or \"{NewName}\"",
                    OrgId,
                    application.Id,
                    application.Year,
                    application.RegistrationReference,
                    application.AccreditationReference,
                    application.OrganisationName,
                    AccreditationYear,
                    OrgId,
                    OldName,
                    NewName
                );
                return null;
            }
            applicationPlans.Add(
                new ApplicationPlan(
                    id,
                    application.ApplicationReference,
                    application.RegistrationReference,
                    application.AccreditationReference,
                    application.ApplicationStatus,
                    application.OrganisationName == NewName
                )
            );
        }

        if (!found.Any(a => a.RegistrationReference == RegistrationReference))
        {
            logger.LogError(
                "RA-764 {OrgId}: none of its {Count} {Year} applications holds registration "
                    + "{RegistrationReference}",
                OrgId,
                found.Count,
                AccreditationYear,
                RegistrationReference
            );
            return null;
        }

        if (organisations is null)
            return new Plan(applicationPlans, null);

        var organisationPlan = await PlanOrganisationAsync(
            organisations,
            reExOrganisationIds[0],
            cancellationToken
        );
        return organisationPlan.Ok ? new Plan(applicationPlans, organisationPlan.Plan) : null;
    }

    private async Task<(bool Ok, OrganisationPlan? Plan)> PlanOrganisationAsync(
        OrganisationPersistence organisations,
        string reExOrganisationId,
        CancellationToken cancellationToken
    )
    {
        var found = await organisations.FindByReExOrganisationIdOrNumberAsync(
            reExOrganisationId,
            OrgId,
            cancellationToken
        );
        if (found.Count == 0)
            return (true, null);

        if (found.Count > 1)
        {
            logger.LogError(
                "RA-764 {OrgId}: {Count} domain model organisations match ReEx organisation "
                    + "{ReExOrganisationId} or number {OrgId}; expected at most one",
                OrgId,
                found.Count,
                reExOrganisationId,
                OrgId
            );
            return (false, null);
        }

        var organisation = found[0];
        if (!IsTargetOrganisation(organisation, reExOrganisationId))
        {
            logger.LogError(
                "RA-764 {OrgId}: domain model organisation {OrganisationId} is ReEx organisation "
                    + "{ActualReExOrganisationId}, number {OrganisationNumber}, name "
                    + "\"{OrganisationName}\"; expected {ReExOrganisationId}, {OrgId} and the "
                    + "name \"{OldName}\" or \"{NewName}\"",
                OrgId,
                organisation.Id,
                organisation.ReExOrganisationId,
                organisation.OrganisationNumber,
                organisation.Name,
                reExOrganisationId,
                OrgId,
                OldName,
                NewName
            );
            return (false, null);
        }

        return (true, new OrganisationPlan(organisation.Id, organisation.Name == NewName));
    }

    // Every reference carries the organisation number at characters 5-10 (see
    // AccreditationReferenceDerivation), so a record holding a reference for any other organisation
    // is not ours, whatever its OrgId says.
    private static bool IsTargetApplication(AccreditationApplicationModel application) =>
        application.OrgId is null or OrgId
        && application.Year == AccreditationYear
        && IsNullOrForOrg(application.RegistrationReference)
        && IsNullOrForOrg(application.AccreditationReference)
        && application.OrganisationName is OldName or NewName;

    private static bool IsNullOrForOrg(string? reference) =>
        reference is null
        || (reference.Length == 17 && reference[5..11] == OrgId.ToString("D6"));

    // A domain organisation seeded before it had a number holds none, so a null number is accepted.
    private static bool IsTargetOrganisation(
        OrganisationEntity organisation,
        string reExOrganisationId
    ) =>
        organisation.ReExOrganisationId == reExOrganisationId
        && organisation.OrganisationNumber is null or OrgId
        && organisation.Name is OldName or NewName;

    private async Task ReportAndApplyAsync(
        IAccreditationApplicationPersistence persistence,
        ApplicationPlan plan,
        string mode,
        string? confirmedBy,
        CancellationToken cancellationToken
    )
    {
        if (plan.AlreadyRenamed)
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "RA-764 {Mode}: {OrgId} application {ApplicationId} ({ApplicationReference}, "
                        + "{RegistrationReference}/{AccreditationReference}, {Status}) already "
                        + "holds \"{NewName}\"; skipped",
                    mode,
                    OrgId,
                    plan.Id,
                    plan.ApplicationReference,
                    plan.RegistrationReference,
                    plan.AccreditationReference,
                    plan.Status,
                    NewName
                );
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-764 {Mode}: {OrgId} application {ApplicationId} ({ApplicationReference}, "
                    + "{RegistrationReference}/{AccreditationReference}, {Status}): "
                    + "\"{OldName}\" -> \"{NewName}\"",
                mode,
                OrgId,
                plan.Id,
                plan.ApplicationReference,
                plan.RegistrationReference,
                plan.AccreditationReference,
                plan.Status,
                OldName,
                NewName
            );

        if (confirmedBy is null)
            return;

        var updated = await persistence.UpdateFieldsAsync(
            plan.Id,
            Builders<AccreditationApplicationModel>.Filter.Eq(a => a.OrganisationName, OldName),
            Builders<AccreditationApplicationModel>.Update.Set(a => a.OrganisationName, NewName),
            cancellationToken: cancellationToken
        );
        LogApplied("application", plan.Id, updated is not null, confirmedBy);
    }

    private async Task ReportAndApplyAsync(
        OrganisationPersistence persistence,
        OrganisationPlan plan,
        string mode,
        string? confirmedBy,
        CancellationToken cancellationToken
    )
    {
        if (plan.AlreadyRenamed)
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "RA-764 {Mode}: {OrgId} domain model organisation {OrganisationId} already "
                        + "holds \"{NewName}\"; skipped",
                    mode,
                    OrgId,
                    plan.Id,
                    NewName
                );
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-764 {Mode}: {OrgId} domain model organisation {OrganisationId}: "
                    + "\"{OldName}\" -> \"{NewName}\"",
                mode,
                OrgId,
                plan.Id,
                OldName,
                NewName
            );

        if (confirmedBy is null)
            return;

        var renamed = await persistence.RenameAsync(plan.Id, OldName, NewName, cancellationToken);
        LogApplied("domain model organisation", plan.Id, renamed, confirmedBy);
    }

    private void LogApplied(string record, ObjectId id, bool written, string confirmedBy)
    {
        if (!written)
        {
            logger.LogError(
                "RA-764 {OrgId} {Record} {Id} no longer holds \"{OldName}\"; it changed after "
                    + "planning, so nothing was written to it. Re-run the dry run to see its "
                    + "current state.",
                OrgId,
                record,
                id,
                OldName
            );
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-764 APPLIED: {OrgId} {Record} {Id}: \"{OldName}\" -> \"{NewName}\". Spot check "
                    + "confirmed by {ConfirmedBy}",
                OrgId,
                record,
                id,
                OldName,
                NewName,
                confirmedBy
            );
    }
}
