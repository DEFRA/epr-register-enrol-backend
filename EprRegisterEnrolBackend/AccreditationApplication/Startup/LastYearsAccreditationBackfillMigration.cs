using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-763: bound from Migrations:Ra763LastYearsAccreditationBackfill. Every flag defaults off, so the
// migration is a no-op in every environment until deliberately switched on.
public sealed class LastYearsAccreditationBackfillOptions
{
    public const string SectionName = "Migrations:Ra763LastYearsAccreditationBackfill";

    // Off: the migration does nothing at all (not even a read).
    public bool Enabled { get; set; }

    // Off (with Enabled on): dry run - logs what it would add and correct for every 2027 application,
    // writes nothing.
    public bool Apply { get; set; }

    // Who reviewed the dry-run output against the live records. Required before Apply takes
    // effect; logged with every applied change so the authorisation outlives the env var.
    public string? SpotCheckConfirmedBy { get; set; }
}

// RA-763: 2027 accreditation numbers are derived from last year's accreditation number, which the
// seed now keeps on the application (LastYearsAccreditation). Applications seeded before that hold
// none, so their numbers would be derived from the registration number instead. This one-off gives
// every existing 2027 application its 2026 accreditation number from the 5 November 2026 public
// register (PublicRegister2026), and corrects the one 2027 number already issued from the wrong
// source (Corrections). It never creates an application.
//
// For each 2027 application, oldest first:
//   1. Find its register row by RegistrationReference, else by one of its
//      PreviousRegistrationNumbers (case management can regenerate the number). A row in
//      PublicRegister2026.HeldBack is a register error awaiting confirmation: reported, not used.
//   2. Check the row is this application's registration: the org digits in the registration number
//      (not the register's Org ID column, which is wrong on some rows), the material code and
//      reprocessor or exporter. A mismatch is reported and the application skipped.
//   3. Last year's number: added where the application holds none; left alone where it holds the
//      same; reported, never overwritten, where it holds a different one. A row with no accreditation
//      means the operator was registered only in 2026: nothing to add, and issuance rightly falls back
//      to the registration number.
//   4. An issued 2027 number is compared with the one derived from the register. A mismatch named in
//      Corrections is corrected; any other is reported only.
// Applications with no register row are reported: their numbers would be derived from the
// registration number, so someone should confirm each was registered only.
//
// Every application is planned before anything is written. Each write is a single targeted update
// guarded on the application still holding what was planned from, so a re-run is a no-op and a
// concurrent write is never clobbered. A correction puts the old number onto
// PreviousAccreditationNumbers via $addToSet (the RA-448 regenerate audit trail), as RA-691 did.
// Case management keeps its own copy and audit entries, which management-be writes.
//
// Writes the legacy applications only, so it runs before DomainModel:DualWrite is turned on; with it
// on, Apply drops back to a dry run (as RA-691). The domain model migration carries the field
// across later: LegacyApplicationMapper onto the Application, ApplicationGroupMigrator onto the
// Registration.
//
// A BackgroundService, so a Mongo failure here logs rather than crash-looping the host. The
// persistence is resolved only once the migration is enabled.
public class LastYearsAccreditationBackfillMigration(
    IServiceProvider serviceProvider,
    IOptions<LastYearsAccreditationBackfillOptions> options,
    ILogger<LastYearsAccreditationBackfillMigration> logger
) : BackgroundService
{
    internal const int AccreditationYear = 2027;
    internal const int LastYear = AccreditationYear - 1;

    internal sealed record Correction(
        string OrganisationName,
        string RegistrationNumber,
        string Incorrect,
        string Corrected
    );

    // Issued 2027 numbers to correct, each confirmed against the register by a person. Any other issued
    // number that differs from the register is reported, not corrected.
    internal static readonly IReadOnlyList<Correction> Corrections =
    [
        new(
            "CLEARFIELD ENVIROTECH LIMITED",
            "R26ER5003240079PL",
            "A27ER5003240079PL",
            "A27ER5003240164PL"
        ),
    ];

    internal enum RunResult
    {
        Disabled,
        DryRun,
        Applied,
    }

    internal enum Outcome
    {
        Added,
        AlreadyHeld,
        RegisteredOnly,
        HoldsADifferentNumber,
        IdentityMismatch,
        HeldBack,
        NotOnTheRegister,
    }

    internal enum IssuedNumber
    {
        NotIssued,
        Correct,
        Corrected,
        AlreadyCorrected,
        WrongReportedOnly,
        CorrectionBlocked,
    }

    private sealed record Plan(
        AccreditationApplicationModel Application,
        ObjectId Id,
        Outcome Outcome,
        string? RegisterAccreditationNumber,
        IssuedNumber Issued,
        string? ExpectedReference,
        string Detail
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
            // Every plan runs before any write, and each write is independently guarded, so a
            // failure part-way leaves a state the next run re-plans from safely.
            logger.LogError(ex, "RA-763 last year's accreditation backfill failed");
        }
    }

    // Extracted so tests can await the run deterministically - BackgroundService's StartAsync only
    // kicks ExecuteAsync off as a background task.
    internal async Task<RunResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return RunResult.Disabled;

        var confirmedBy = AuthorisedReviewer(options.Value);
        var mode = confirmedBy is null ? "DRY RUN (nothing will be written)" : "APPLY";
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-763 last year's accreditation backfill starting. Mode={Mode}",
                mode
            );

        var persistence =
            serviceProvider.GetRequiredService<IAccreditationApplicationPersistence>();
        var applications = await persistence.GetByYearAsync(AccreditationYear, cancellationToken);

        var plans = new List<Plan>();
        foreach (var application in applications)
        {
            if (application.Id is { } id)
                plans.Add(await PlanAsync(persistence, application, id, cancellationToken));
        }

        foreach (var plan in plans)
            await ReportAndApplyAsync(persistence, plan, mode, confirmedBy, cancellationToken);

        ReportUnusedCorrections(plans);
        ReportSummary(plans, mode);
        return confirmedBy is null ? RunResult.DryRun : RunResult.Applied;
    }

    // Apply takes effect only with a named reviewer, and only with dual-write off; otherwise the run
    // stays a dry run.
    private string? AuthorisedReviewer(LastYearsAccreditationBackfillOptions settings)
    {
        if (!settings.Apply)
            return null;

        if (string.IsNullOrWhiteSpace(settings.SpotCheckConfirmedBy))
        {
            logger.LogError(
                "RA-763 last year's accreditation backfill: Apply is set but SpotCheckConfirmedBy is "
                    + "not, so running as a DRY RUN. Set it to whoever reviewed the dry-run output "
                    + "against the live records."
            );
            return null;
        }

        if (serviceProvider.GetService<IOptions<DomainModelConfig>>()?.Value.DualWrite == true)
        {
            logger.LogError(
                "RA-763 last year's accreditation backfill: DomainModel:DualWrite is on, and this "
                    + "backfill writes the legacy application without the domain model, so running as "
                    + "a DRY RUN. Apply it with DualWrite off."
            );
            return null;
        }

        return settings.SpotCheckConfirmedBy;
    }

    // --- planning ---------------------------------------------------------------------------------------------------

    private async Task<Plan> PlanAsync(
        IAccreditationApplicationPersistence persistence,
        AccreditationApplicationModel application,
        ObjectId id,
        CancellationToken cancellationToken
    )
    {
        var numbers = new[] { application.RegistrationReference }
            .Concat(application.PreviousRegistrationNumbers)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        if (numbers.Find(PublicRegister2026.HeldBack.ContainsKey) is { } heldBack)
        {
            return NotPlanned(
                application,
                id,
                Outcome.HeldBack,
                $"register row {heldBack} is held back as a register error until it is confirmed"
            );
        }

        var registrationNumber = numbers.Find(PublicRegister2026.ByRegistrationNumber.ContainsKey);
        if (registrationNumber is null)
        {
            return NotPlanned(
                application,
                id,
                Outcome.NotOnTheRegister,
                numbers.Count == 0
                    ? "it holds no registration number"
                    : $"none of its registration numbers ({string.Join(", ", numbers)}) is on the register"
            );
        }

        if (IdentityMismatch(application, registrationNumber) is { } mismatch)
            return NotPlanned(application, id, Outcome.IdentityMismatch, mismatch);

        var row = PublicRegister2026.ByRegistrationNumber[registrationNumber];
        var held = application.LastYearsAccreditation;
        var (outcome, detail) = row.AccreditationNumber switch
        {
            null => (
                Outcome.RegisteredOnly,
                $"register row {registrationNumber} has no accreditation, so the operator was registered only in {LastYear}"
            ),
            var number when held is null => (
                Outcome.Added,
                $"register row {registrationNumber}: last year's accreditation {number}"
            ),
            var number when held.Year == LastYear && held.AccreditationNumber == number => (
                Outcome.AlreadyHeld,
                $"already holds last year's accreditation {number}"
            ),
            var number => (
                Outcome.HoldsADifferentNumber,
                $"holds {held.Year} accreditation {held.AccreditationNumber}, but register row "
                    + $"{registrationNumber} says {LastYear} accreditation {number}; left as it is"
            ),
        };

        var (issued, expected, issuedDetail) = await PlanIssuedNumberAsync(
            persistence,
            application,
            registrationNumber,
            row.AccreditationNumber,
            cancellationToken
        );
        return new Plan(
            application,
            id,
            outcome,
            row.AccreditationNumber,
            issued,
            expected,
            issuedDetail is null ? detail : $"{detail}; {issuedDetail}"
        );
    }

    private static Plan NotPlanned(
        AccreditationApplicationModel application,
        ObjectId id,
        Outcome outcome,
        string detail
    ) =>
        new(
            application,
            id,
            outcome,
            null,
            application.AccreditationReference is null
                ? IssuedNumber.NotIssued
                : IssuedNumber.WrongReportedOnly,
            null,
            application.AccreditationReference is null
                ? detail
                : $"{detail}; issued {application.AccreditationReference} could not be checked"
        );

    // The registration number carries its own identity: R + YY + agency + R|X + org (6) + sequence (4) + material.
    // The register's Org ID column is not used: it is wrong on some rows.
    internal static string? IdentityMismatch(
        AccreditationApplicationModel application,
        string registrationNumber
    )
    {
        var problems = new List<string>();
        var orgDigits = registrationNumber[5..11];
        if (application.OrgId is { } orgId && orgId.ToString("D6") != orgDigits)
            problems.Add($"its org is {orgId} but the registration number's is {orgDigits}");

        var processType = RegulatoryReferenceCodes.ProcessTypeLetterFor(application.IsExporter);
        if (registrationNumber[4] != processType)
        {
            problems.Add(
                $"it is {(application.IsExporter ? "an exporter" : "a reprocessor")} but the "
                    + $"registration number is {registrationNumber[4]}"
            );
        }

        var materialCode = registrationNumber[^2..];
        if (!MaterialCodesFor(application).Contains(materialCode))
        {
            problems.Add(
                $"its material is {application.MaterialType} but the registration number's is {materialCode}"
            );
        }

        return problems.Count == 0
            ? null
            : $"register row {registrationNumber} is not this application's registration: "
                + string.Join("; ", problems);
    }

    // Glass splits into GR and GO by recycling process; an application that never recorded the process
    // can be either.
    private static string[] MaterialCodesFor(AccreditationApplicationModel application) =>
        application.MaterialType == MaterialType.Glass && application.GlassRecyclingProcess is null
            ? ["GR", "GO"]
            : [
                RegulatoryReferenceCodes.MaterialCodeFor(
                    application.MaterialType,
                    application.GlassRecyclingProcess
                ),
            ];

    private async Task<(IssuedNumber, string?, string?)> PlanIssuedNumberAsync(
        IAccreditationApplicationPersistence persistence,
        AccreditationApplicationModel application,
        string registrationNumber,
        string? lastYearsAccreditation,
        CancellationToken cancellationToken
    )
    {
        var issued = application.AccreditationReference;
        if (issued is null)
            return (IssuedNumber.NotIssued, null, null);

        var expected = (
            lastYearsAccreditation is null
                ? AccreditationReferenceDerivation.Derive(
                    registrationNumber,
                    AccreditationReferenceSourceKind.Registration,
                    AccreditationYear
                )
                : AccreditationReferenceDerivation.Derive(
                    lastYearsAccreditation,
                    AccreditationReferenceSourceKind.PreviousAccreditation,
                    AccreditationYear
                )
        ).Reference;

        var correction = Corrections.FirstOrDefault(c =>
            c.RegistrationNumber == registrationNumber
        );
        if (issued == expected)
        {
            return correction is not null && issued == correction.Corrected
                ? (IssuedNumber.AlreadyCorrected, expected, $"issued {issued} is already corrected")
                : (IssuedNumber.Correct, expected, $"issued {issued} matches the register");
        }

        if (correction is null || correction.Incorrect != issued || correction.Corrected != expected)
        {
            return (
                IssuedNumber.WrongReportedOnly,
                expected,
                $"issued {issued}, but the register gives {expected ?? "no valid number"}; reported only"
            );
        }

        var holders = await persistence.GetByAccreditationReferenceAsync(
            correction.Corrected,
            cancellationToken
        );
        if (holders.Count > 0)
        {
            return (
                IssuedNumber.CorrectionBlocked,
                expected,
                $"issued {issued} should be {correction.Corrected}, but application "
                    + $"{holders[0].Id} already holds that; not corrected"
            );
        }

        return (
            IssuedNumber.Corrected,
            expected,
            $"issued {issued} is corrected to {correction.Corrected}"
        );
    }

    // --- reporting and writing --------------------------------------------------------------------------------------

    private async Task ReportAndApplyAsync(
        IAccreditationApplicationPersistence persistence,
        Plan plan,
        string mode,
        string? confirmedBy,
        CancellationToken cancellationToken
    )
    {
        var level = NeedsAPerson(plan) ? LogLevel.Warning : LogLevel.Information;
        if (logger.IsEnabled(level))
        {
            logger.Log(
                level,
                "RA-763 {Mode}: application {ApplicationId} ({ApplicationReference}, org {OrgId}, "
                    + "{MaterialType}, registration {RegistrationReference}): {Outcome}, issued number "
                    + "{IssuedNumber}. {Detail}",
                mode,
                plan.Id,
                plan.Application.ApplicationReference,
                plan.Application.OrgId,
                plan.Application.MaterialType,
                plan.Application.RegistrationReference,
                plan.Outcome,
                plan.Issued,
                plan.Detail
            );
        }

        if (confirmedBy is null)
            return;

        if (plan.Outcome == Outcome.Added)
            await AddLastYearsAccreditationAsync(persistence, plan, confirmedBy, cancellationToken);

        if (plan.Issued == IssuedNumber.Corrected)
            await CorrectIssuedNumberAsync(persistence, plan, confirmedBy, cancellationToken);
    }

    private static bool NeedsAPerson(Plan plan) =>
        plan.Outcome
            is Outcome.HoldsADifferentNumber
                or Outcome.IdentityMismatch
                or Outcome.HeldBack
                or Outcome.NotOnTheRegister
        || plan.Issued is IssuedNumber.WrongReportedOnly or IssuedNumber.CorrectionBlocked;

    private async Task AddLastYearsAccreditationAsync(
        IAccreditationApplicationPersistence persistence,
        Plan plan,
        string confirmedBy,
        CancellationToken cancellationToken
    )
    {
        var updated = await persistence.UpdateFieldsAsync(
            plan.Id,
            Builders<AccreditationApplicationModel>.Filter.Eq(a => a.LastYearsAccreditation, null),
            Builders<AccreditationApplicationModel>.Update.Set(
                a => a.LastYearsAccreditation,
                new AccreditationNumberForYearModel
                {
                    Year = LastYear,
                    AccreditationNumber = plan.RegisterAccreditationNumber!,
                }
            ),
            cancellationToken: cancellationToken
        );

        if (updated is null)
        {
            logger.LogError(
                "RA-763 application {ApplicationId} gained last year's accreditation after planning, so "
                    + "nothing was written to it. Re-run the dry run to see its current state.",
                plan.Id
            );
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "RA-763 APPLIED: application {ApplicationId} ({ApplicationReference}) now holds "
                    + "{Year} accreditation {AccreditationNumber}. Spot check confirmed by {ConfirmedBy}",
                plan.Id,
                plan.Application.ApplicationReference,
                LastYear,
                plan.RegisterAccreditationNumber,
                confirmedBy
            );
        }
    }

    private async Task CorrectIssuedNumberAsync(
        IAccreditationApplicationPersistence persistence,
        Plan plan,
        string confirmedBy,
        CancellationToken cancellationToken
    )
    {
        var incorrect = plan.Application.AccreditationReference!;
        var corrected = plan.ExpectedReference!;
        var updated = await persistence.UpdateFieldsAsync(
            plan.Id,
            Builders<AccreditationApplicationModel>.Filter.Eq(
                a => a.AccreditationReference,
                incorrect
            ),
            Builders<AccreditationApplicationModel>
                .Update.Set(a => a.AccreditationReference, corrected)
                .AddToSet(a => a.PreviousAccreditationNumbers, incorrect),
            cancellationToken: cancellationToken
        );

        if (updated is null)
        {
            logger.LogError(
                "RA-763 application {ApplicationId} no longer holds {Incorrect}; it changed after "
                    + "planning, so nothing was written to it. Re-run the dry run to see its current state.",
                plan.Id,
                incorrect
            );
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "RA-763 APPLIED: application {ApplicationId} ({ApplicationReference}) accreditation "
                    + "number {Incorrect} -> {Corrected}. Spot check confirmed by {ConfirmedBy}",
                plan.Id,
                plan.Application.ApplicationReference,
                incorrect,
                corrected,
                confirmedBy
            );
        }
    }

    // A correction that matched no application is worth knowing: the record may have changed shape since it
    // was listed.
    private void ReportUnusedCorrections(IReadOnlyList<Plan> plans)
    {
        foreach (var correction in Corrections)
        {
            var used = plans.Any(p =>
                p.Issued
                    is IssuedNumber.Corrected
                        or IssuedNumber.AlreadyCorrected
                        or IssuedNumber.CorrectionBlocked
                && p.Application.AccreditationReference is { } issued
                && (issued == correction.Incorrect || issued == correction.Corrected)
            );
            if (!used)
            {
                logger.LogWarning(
                    "RA-763 {OrganisationName}: no 2027 application on registration {RegistrationNumber} "
                        + "holds {Incorrect} or {Corrected}; nothing to correct",
                    correction.OrganisationName,
                    correction.RegistrationNumber,
                    correction.Incorrect,
                    correction.Corrected
                );
            }
        }
    }

    private void ReportSummary(IReadOnlyList<Plan> plans, string mode)
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;

        var outcomes = string.Join(
            ", ",
            Enum.GetValues<Outcome>().Select(o => $"{o}={plans.Count(p => p.Outcome == o)}")
        );
        var issued = string.Join(
            ", ",
            Enum.GetValues<IssuedNumber>().Select(i => $"{i}={plans.Count(p => p.Issued == i)}")
        );
        logger.LogInformation(
            "RA-763 last year's accreditation backfill complete. Mode={Mode}, applications={Count}. "
                + "Last year's accreditation: {Outcomes}. Issued numbers: {Issued}",
            mode,
            plans.Count,
            outcomes,
            issued
        );
    }
}
