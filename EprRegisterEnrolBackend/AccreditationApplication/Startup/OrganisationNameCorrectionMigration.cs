using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Utils.Mongo;
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
// Matching rule: the 2027 applications are found by OrgId and Year (and, for drafts not yet
// holding their OrgId, by the ReEx organisation id), then every one must be Year 2027, hold OrgId
// null or 500069, the same single ReEx organisation id, and the old or the new name. 500069 holds
// two registrations, so references are checked against an explicit allow-list rather than derived
// from the organisation number (the EA registration's reference carries 500226, not 500069):
//
//   registration (NIEA)  R26NR5000690003GR  2026 accreditation A26NR5000690003GR
//   registration (EA)    R26ER5002260055GR  2026 accreditation A26ER5002260023GR
//
// A record's registration reference must be null or one of the two. Its accreditation reference
// must be null or a 2027 reference for its own registration: derived for 2027
// (AccreditationReferenceDerivation: leading character 'A', characters 2-3 the year, the rest
// copied) from that registration's reference or its 2026 accreditation, with the 4-digit sequence
// (characters 12-15) accepted as any ASCII digits - A27NR500069####GR for the NIEA registration,
// A27ER500226####GR for the EA one. The sequence is tolerated because a 2027 number can be issued
// with a sequence other than the derived one (RA-691). A cross-pair aborts. A draft with no
// registration yet may hold either shape. Management-be applies the same rule. At least one 2027 record must hold one of
// the two registrations, proving these are the accreditations we were asked about. The domain
// organisation is found by the applications' ReEx organisation id or by number 500069; more than
// one, a mismatched key, or an unexpected name aborts (no reference is checked there). Every record
// is planned before anything is written, so an abort means no writes at all.
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
// run - so after a partial run the flag must stay on and the migration re-run until the log shows
// the Organisation renamed. Because both copies are written, this does not need dual-write to be
// off (contrast RA-691). The Organisation is organisation-wide, so renaming it makes 500069's
// applications for other years (which keep the old name) differ from the read model; each gets a
// SeededDetailsDiffer conflict so the reconciler accepts that (RecordOtherYearConflictsAsync).
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
    internal const string NiRegistrationReference = "R26NR5000690003GR";
    internal const string NiAccreditation2026Reference = "A26NR5000690003GR";
    internal const string EaRegistrationReference = "R26ER5002260055GR";
    internal const string EaAccreditation2026Reference = "A26ER5002260023GR";

    // Each allowed registration reference, with the 2027 accreditation references derived from it
    // and from its 2026 accreditation (see the class comment). An accreditation matches one of these
    // in everything but its 4-digit sequence.
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedReferences =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [NiRegistrationReference] = Derived2027(
                NiRegistrationReference,
                NiAccreditation2026Reference
            ),
            [EaRegistrationReference] = Derived2027(
                EaRegistrationReference,
                EaAccreditation2026Reference
            ),
        };

    // Characters 12-15 (1-based) of a reference: the sequence, tolerated as any 4 ASCII digits.
    private const int SequenceStart = 11;
    private const int SequenceEnd = 15;

    private static string[] Derived2027(string registration, string accreditation2026) =>
        [
            Derive(registration, AccreditationReferenceSourceKind.Registration),
            Derive(accreditation2026, AccreditationReferenceSourceKind.PreviousAccreditation),
        ];

    private static string Derive(string source, AccreditationReferenceSourceKind kind) =>
        AccreditationReferenceDerivation.Derive(source, kind, AccreditationYear).Reference
        ?? throw new InvalidOperationException($"RA-764: {source} is not a valid reference");

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

    // OtherYears: 500069's applications for other years that keep the old name (see RecordOtherYearConflictsAsync).
    private sealed record OrganisationPlan(
        ObjectId Id,
        bool AlreadyRenamed,
        IReadOnlyList<AccreditationApplicationModel> OtherYears
    );

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
        if (!options.Value.Enabled)
            return RunResult.Disabled;

        // Null for a dry run; otherwise who authorised the apply.
        var confirmedBy = AuthorisedReviewer(options.Value);
        var mode = confirmedBy is null ? "DRY RUN (nothing will be written)" : "APPLY";
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

        foreach (var application in plan.Applications)
            await ReportAndApplyAsync(
                "application",
                application.Id,
                $" ({application.ApplicationReference}, {application.RegistrationReference}/"
                    + $"{application.AccreditationReference}, {application.Status})",
                application.AlreadyRenamed,
                mode,
                confirmedBy,
                async () =>
                    await applications.UpdateFieldsAsync(
                        application.Id,
                        Builders<AccreditationApplicationModel>.Filter.Eq(
                            a => a.OrganisationName,
                            OldName
                        ),
                        Builders<AccreditationApplicationModel>.Update.Set(
                            a => a.OrganisationName,
                            NewName
                        ),
                        cancellationToken: cancellationToken
                    ) is not null
            );

        if (plan.Organisation is { } organisation)
        {
            var renamed = await ReportAndApplyAsync(
                "domain model organisation",
                organisation.Id,
                "",
                organisation.AlreadyRenamed,
                mode,
                confirmedBy,
                () => organisations!.RenameAsync(organisation.Id, OldName, NewName, cancellationToken)
            );
            // Only once the Organisation holds the new name do the other years differ from it.
            if (renamed || confirmedBy is null)
                await RecordOtherYearConflictsAsync(
                    organisation.OtherYears,
                    mode,
                    confirmedBy,
                    cancellationToken
                );
        }
        else if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-764 {Mode}: no domain model organisation for {OrgId}; skipped",
                mode,
                OrgId
            );

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("RA-764 organisation name correction complete. Mode={Mode}", mode);
        return confirmedBy is null ? RunResult.DryRun : RunResult.Applied;
    }

    // Apply takes effect only with a named reviewer; without one the run stays a dry run.
    private string? AuthorisedReviewer(OrganisationNameCorrectionOptions settings)
    {
        if (!settings.Apply)
            return null;
        if (!string.IsNullOrWhiteSpace(settings.SpotCheckConfirmedBy))
            return settings.SpotCheckConfirmedBy;

        logger.LogError(
            "RA-764 organisation name correction: Apply is set but SpotCheckConfirmedBy is not, "
                + "so running as a DRY RUN. Set it to whoever reviewed the dry-run output against "
                + "the live records."
        );
        return null;
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
                    "RA-764 {OrgId}: application {ApplicationId} is Year {Year}, OrgId "
                        + "{ApplicationOrgId}, ReEx organisation {ReExOrganisationId}, status "
                        + "{ApplicationStatus}, registration {RegistrationReference}, accreditation "
                        + "{AccreditationReference}, name \"{OrganisationName}\"; expected Year "
                        + "{ExpectedYear}, OrgId null or {ExpectedOrgId}, registration null or one "
                        + "of {AllowedRegistrations}, an accreditation null or one for its "
                        + "registration ({AllowedAccreditations}), and the name \"{OldName}\" or "
                        + "\"{NewName}\"",
                    OrgId,
                    application.Id,
                    application.Year,
                    application.OrgId,
                    application.OrganisationId,
                    application.ApplicationStatus,
                    application.RegistrationReference,
                    application.AccreditationReference,
                    application.OrganisationName,
                    AccreditationYear,
                    OrgId,
                    string.Join(", ", AllowedReferences.Keys),
                    string.Join(
                        "; ",
                        AllowedReferences.Select(r =>
                            $"{r.Key}: {string.Join(" or ", r.Value.Select(Shape).Distinct())}"
                        )
                    ),
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

        if (
            !found.Any(a =>
                a.RegistrationReference is { } registration
                && AllowedReferences.ContainsKey(registration)
            )
        )
        {
            logger.LogError(
                "RA-764 {OrgId}: none of its {Count} {Year} applications holds registration "
                    + "{AllowedRegistrations}",
                OrgId,
                found.Count,
                AccreditationYear,
                string.Join(" or ", AllowedReferences.Keys)
            );
            return null;
        }

        if (organisations is null)
            return new Plan(applicationPlans, null);

        var otherYears = byReExId
            .Where(a => a.Year != AccreditationYear && a.Id is not null && a.OrganisationName != NewName)
            .ToList();
        var organisationPlan = await PlanOrganisationAsync(
            organisations,
            reExOrganisationIds[0],
            otherYears,
            cancellationToken
        );
        return organisationPlan.Ok ? new Plan(applicationPlans, organisationPlan.Plan) : null;
    }

    private async Task<(bool Ok, OrganisationPlan? Plan)> PlanOrganisationAsync(
        OrganisationPersistence organisations,
        string reExOrganisationId,
        IReadOnlyList<AccreditationApplicationModel> otherYears,
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
                    + "{ReExOrganisationId} or number {ExpectedOrgId}; expected at most one",
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
                    + "\"{OrganisationName}\"; expected {ReExOrganisationId}, {ExpectedOrgId} and the "
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

        return (true, new OrganisationPlan(organisation.Id, organisation.Name == NewName, otherYears));
    }

    // References are checked against the allow-list, not the organisation number they carry: the EA
    // registration's reference carries 500226 (see the class comment).
    private static bool IsTargetApplication(AccreditationApplicationModel application) =>
        application.OrgId is null or OrgId
        && application.Year == AccreditationYear
        && HasAllowedReferences(application.RegistrationReference, application.AccreditationReference)
        && application.OrganisationName is OldName or NewName;

    private static bool HasAllowedReferences(string? registration, string? accreditation)
    {
        if (registration is null)
            return accreditation is null
                || AllowedReferences.Values.Any(derived => MatchesAny(accreditation, derived));
        return AllowedReferences.TryGetValue(registration, out var forRegistration)
            && (accreditation is null || MatchesAny(accreditation, forRegistration));
    }

    // Ordinal: the same as a derived reference except, perhaps, its 4-digit sequence.
    private static bool MatchesAny(string accreditation, IReadOnlyList<string> derived) =>
        accreditation.Length == 17
        && accreditation[SequenceStart..SequenceEnd].All(char.IsAsciiDigit)
        && derived.Any(d =>
            string.Equals(Shape(accreditation), Shape(d), StringComparison.Ordinal)
        );

    // A reference with its sequence masked, e.g. A27NR500069####GR.
    private static string Shape(string reference) =>
        $"{reference[..SequenceStart]}####{reference[SequenceEnd..]}";

    // A domain organisation seeded before it had a number holds none, so a null number is accepted.
    private static bool IsTargetOrganisation(
        OrganisationEntity organisation,
        string reExOrganisationId
    ) =>
        organisation.ReExOrganisationId == reExOrganisationId
        && organisation.OrganisationNumber is null or OrgId
        && organisation.Name is OldName or NewName;

    // Logs one planned rename (or skip) and, when confirmedBy is set (an authorised apply run), runs
    // the guarded write. Returns whether the record now holds the new name.
    private async Task<bool> ReportAndApplyAsync(
        string record,
        ObjectId id,
        string details,
        bool alreadyRenamed,
        string mode,
        string? confirmedBy,
        Func<Task<bool>> write
    )
    {
        if (alreadyRenamed)
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "RA-764 {Mode}: {OrgId} {Record} {Id}{Details} already holds \"{NewName}\"; skipped",
                    mode,
                    OrgId,
                    record,
                    id,
                    details,
                    NewName
                );
            return true;
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-764 {Mode}: {OrgId} {Record} {Id}{Details}: \"{OldName}\" -> \"{NewName}\"",
                mode,
                OrgId,
                record,
                id,
                details,
                OldName,
                NewName
            );

        if (confirmedBy is null)
            return false;

        var written = await write();
        LogApplied(record, id, written, confirmedBy);
        return written;
    }

    // The domain Organisation is organisation-wide, and the read model projects its name onto every
    // application, so once it is renamed 500069's applications for other years (which keep the name
    // they were started with) differ from it. A SeededDetailsDiffer conflict on each is the record
    // the reconciler accepts that difference by - the same one the migration writes when an
    // application's name differs from its Organisation's. Guarded on one already being recorded, so
    // a re-run adds none.
    private async Task RecordOtherYearConflictsAsync(
        IReadOnlyList<AccreditationApplicationModel> otherYears,
        string mode,
        string? confirmedBy,
        CancellationToken cancellationToken
    )
    {
        var conflicts = serviceProvider.GetService<MigrationConflictPersistence>();
        if (conflicts is null || otherYears.Count == 0)
            return;

        using var session = await serviceProvider
            .GetRequiredService<IMongoDbClientFactory>()
            .GetClient()
            .StartSessionAsync(cancellationToken: cancellationToken);
        foreach (var other in otherYears)
        {
            var id = other.Id!.Value;
            var recorded = await conflicts.ExistsAsync(
                MigrationConflictKind.SeededDetailsDiffer,
                id,
                session,
                cancellationToken
            );
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "RA-764 {Mode}: {OrgId} {Year} application {ApplicationId} keeps "
                        + "\"{OrganisationName}\"; SeededDetailsDiffer conflict {Action}",
                    mode,
                    OrgId,
                    other.Year,
                    id,
                    other.OrganisationName,
                    recorded ? "already recorded; skipped" : "to record"
                );
            if (recorded || confirmedBy is null)
                continue;

            await conflicts.InsertManyAsync(
                [
                    new MigrationConflict
                    {
                        Kind = MigrationConflictKind.SeededDetailsDiffer,
                        ReExRegistrationId = other.RegistrationId ?? "",
                        ApplicationIds = [id],
                        RecordedAt = DateTime.UtcNow,
                    },
                ],
                session,
                cancellationToken
            );
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "RA-764 APPLIED: {OrgId} {Year} application {ApplicationId}: SeededDetailsDiffer "
                        + "conflict recorded. Spot check confirmed by {ConfirmedBy}",
                    OrgId,
                    other.Year,
                    id,
                    confirmedBy
                );
        }
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
