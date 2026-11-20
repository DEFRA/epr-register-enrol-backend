using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-784: bound from Migrations:Ra763LastYearsAccreditationBackfill. Every flag defaults off, so the
// migration is a no-op in every environment until deliberately switched on.
public sealed class LastYearsAccreditationBackfillOptions
{
    public const string SectionName = "Migrations:Ra763LastYearsAccreditationBackfill";

    // Off: the migration does nothing at all (not even a read).
    public bool Enabled { get; set; }

    // Off (with Enabled on): dry run - logs what it would add for every 2027 application, writes nothing.
    public bool Apply { get; set; }

    // Who reviewed the dry-run output against the live records. Required before Apply takes
    // effect; logged with every applied change so the authorisation outlives the env var.
    public string? SpotCheckConfirmedBy { get; set; }
}

// RA-784: a 2027 renewal's accreditation number is last year's accreditation number with the year
// changed (RA-783), and the seed now keeps last year's number on the application
// (LastYearsAccreditationNumber, RA-763). Applications seeded before that hold none, so they would be
// issued a first-time number instead. This one-off gives every existing 2027 application its 2026
// accreditation number from the 5 November 2026 public register (PublicRegister2026). It never
// creates an application and never changes an issued number: numbers issued wrongly are corrected
// separately (RA-785).
//
// Organisation by organisation, and within one by registration number, for each 2027 application:
//   1. An application holding J. and A. Young's registration number as the register prints it,
//      missing its last letter, is reported: a 2027 number can't be issued until its registration
//      number ends PL. It is not corrected here.
//   2. Find its register row by its 2026 registration number: RegistrationReference, else one of
//      its PreviousRegistrationNumbers (case management can regenerate the number). The
//      registration number is the only key: the org digits in it don't always match the org that
//      holds it (Encirc, 500069, holds R26ER5002260055GR), so nothing else is compared. An
//      application whose registration number isn't on the register is reported with its org.
//   3. Last year's number: added where the application holds none; left alone where it holds the
//      same; reported, never overwritten, where it holds a different one. A row with no accreditation
//      means the operator was registered only in 2026: nothing to add, and the 2027 number is a
//      first-time one.
//   4. An issued 2027 number is checked against the register and reported if it doesn't fit: a
//      renewal must keep last year's agency, process type, org and sequence; a first-time number
//      must keep the registration's agency, process type and org. Only reported, never corrected.
// The run ends with a report: every application whose registration number isn't on the register
// (its org and registration number), and every register row no 2027 application used.
//
// Every application is planned before anything is written. Each write is a single targeted update
// guarded on the application still holding no number, so a re-run is a no-op and a concurrent
// write is never clobbered. Each write goes through the dual-write unit of work, so with
// DomainModel:DualWrite on the domain model's Application and Registration take the number in the
// same transaction (ApplicationModelSync), and with it off only the legacy application is written,
// for the domain model migration to carry across later. Case management keeps its own copy, which
// management-be writes.
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

    // The unit of work's name for this write, for its log line and metric.
    internal const string Operation = "Ra784LastYearsAccreditationBackfill";

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
        RegistrationNumberIncomplete,
        NotOnTheRegister,
    }

    internal enum IssuedNumber
    {
        NotIssued,
        FitsTheRegister,
        DoesNotFitTheRegister,
        NotChecked,
    }

    // What happened to an Added plan's write. A dry run attempts none.
    internal enum Write
    {
        Written,
        LostTheRace,
        Failed,
    }

    private sealed record Plan(
        AccreditationApplicationModel Application,
        ObjectId Id,
        Outcome Outcome,
        string? RegisterRegistrationNumber,
        string? RegisterAccreditationNumber,
        IssuedNumber Issued,
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
            logger.LogError(ex, "RA-784 last year's accreditation backfill failed");
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
                "RA-784 last year's accreditation backfill starting. Mode={Mode}",
                mode
            );

        var persistence =
            serviceProvider.GetRequiredService<IAccreditationApplicationPersistence>();
        var unitOfWork = serviceProvider.GetRequiredService<IDualWriteUnitOfWork>();
        var applications = await persistence.GetByYearAsync(AccreditationYear, cancellationToken);

        // Organisation by organisation; a draft without its OrgId yet sorts by its ReEx id after the rest.
        var plans = applications
            .Where(a => a.Id is not null)
            .OrderBy(a => a.OrgId ?? int.MaxValue)
            .ThenBy(a => a.OrganisationId, StringComparer.Ordinal)
            .ThenBy(a => a.RegistrationReference, StringComparer.Ordinal)
            .ThenBy(a => a.Id)
            .Select(a => PlanFor(a, a.Id!.Value))
            .ToList();

        var writes = new List<Write>();
        foreach (var plan in plans)
        {
            if (
                await ReportAndApplyAsync(
                    persistence,
                    unitOfWork,
                    plan,
                    mode,
                    confirmedBy,
                    cancellationToken
                )
                is { } write
            )
                writes.Add(write);
        }

        ReportNotOnTheRegister(plans, mode);
        ReportUnusedRegisterRows(plans, mode);
        ReportSummary(plans, writes, mode);
        return confirmedBy is null ? RunResult.DryRun : RunResult.Applied;
    }

    // Apply takes effect only with a named reviewer; otherwise the run stays a dry run.
    private string? AuthorisedReviewer(LastYearsAccreditationBackfillOptions settings)
    {
        if (!settings.Apply)
            return null;

        if (string.IsNullOrWhiteSpace(settings.SpotCheckConfirmedBy))
        {
            logger.LogError(
                "RA-784 last year's accreditation backfill: Apply is set but SpotCheckConfirmedBy is "
                    + "not, so running as a DRY RUN. Set it to whoever reviewed the dry-run output "
                    + "against the live records."
            );
            return null;
        }

        return settings.SpotCheckConfirmedBy;
    }

    // --- planning ---------------------------------------------------------------------------------------------------

    private static Plan PlanFor(AccreditationApplicationModel application, ObjectId id)
    {
        var numbers = new[] { application.RegistrationReference }
            .Concat(application.PreviousRegistrationNumbers)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        if (application.RegistrationReference == PublicRegister2026.JAndAYoungPublishedRegistrationNumber)
        {
            return NotPlanned(
                application,
                id,
                Outcome.RegistrationNumberIncomplete,
                $"registration number {application.RegistrationReference} is missing its last letter, "
                    + "as the register prints it; it must end PL before a 2027 number can be issued"
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

        var row = PublicRegister2026.ByRegistrationNumber[registrationNumber];

        var held = application.LastYearsAccreditationNumber;
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

        var (issued, issuedDetail) = CheckIssuedNumber(
            application.AccreditationReference,
            registrationNumber,
            row.AccreditationNumber
        );
        return new Plan(
            application,
            id,
            outcome,
            registrationNumber,
            row.AccreditationNumber,
            issued,
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
            null,
            application.AccreditationReference is null
                ? IssuedNumber.NotIssued
                : IssuedNumber.NotChecked,
            application.AccreditationReference is null
                ? detail
                : $"{detail}; issued {application.AccreditationReference} could not be checked"
        );

    // RA-783's rules, for an issued number: a renewal keeps everything of last year's number but the
    // year and material code; a first-time number keeps the registration's agency, process type and
    // org, with a random sequence. The material code is the application's, so it isn't compared (a
    // 2026 glass number can end GL, as Comhairle Nan Eilean Siar's does).
    private static (IssuedNumber, string?) CheckIssuedNumber(
        string? issued,
        string registrationNumber,
        string? lastYearsAccreditationNumber
    )
    {
        if (issued is null)
            return (IssuedNumber.NotIssued, null);

        var year = $"{AccreditationYear % 100:D2}";
        var (source, kept, rule) = lastYearsAccreditationNumber is { } lastYears
            ? (lastYears, lastYears[3..^2], "a renewal of")
            : (registrationNumber, registrationNumber[3..11], "a first-time number from");

        var fits =
            issued.Length == 17 && issued[0] == 'A' && issued[1..3] == year && issued[3..].StartsWith(kept, StringComparison.Ordinal);
        return fits
            ? (IssuedNumber.FitsTheRegister, $"issued {issued} fits the register")
            : (
                IssuedNumber.DoesNotFitTheRegister,
                $"issued {issued} is not {rule} {source}; reported only (RA-785 corrects numbers)"
            );
    }

    // --- reporting and writing --------------------------------------------------------------------------------------

    // Null when no write was attempted: a dry run, or a plan that isn't Added.
    private async Task<Write?> ReportAndApplyAsync(
        IAccreditationApplicationPersistence persistence,
        IDualWriteUnitOfWork unitOfWork,
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
                "RA-784 {Mode}: application {ApplicationId} ({ApplicationReference}, org {OrgId} "
                    + "{OrganisationName}, {MaterialType}, registration {RegistrationReference}): "
                    + "{Outcome}, issued number {IssuedNumber}. {Detail}",
                mode,
                plan.Id,
                plan.Application.ApplicationReference,
                plan.Application.OrgId,
                plan.Application.OrganisationName,
                plan.Application.MaterialType,
                plan.Application.RegistrationReference,
                plan.Outcome,
                plan.Issued,
                plan.Detail
            );
        }

        if (confirmedBy is null || plan.Outcome != Outcome.Added)
            return null;

        return await AddLastYearsAccreditationNumberAsync(
            persistence,
            unitOfWork,
            plan,
            confirmedBy,
            cancellationToken
        );
    }

    private static bool NeedsAPerson(Plan plan) =>
        plan.Outcome
            is Outcome.HoldsADifferentNumber
                or Outcome.RegistrationNumberIncomplete
                or Outcome.NotOnTheRegister
        || plan.Issued is IssuedNumber.DoesNotFitTheRegister or IssuedNumber.NotChecked;

    private async Task<Write> AddLastYearsAccreditationNumberAsync(
        IAccreditationApplicationPersistence persistence,
        IDualWriteUnitOfWork unitOfWork,
        Plan plan,
        string confirmedBy,
        CancellationToken cancellationToken
    )
    {
        var result = await unitOfWork.RunAsync(
            Operation,
            plan.Id.ToString(),
            async (session, token) =>
            {
                var updated = await persistence.UpdateFieldsAsync(
                    plan.Id,
                    Builders<AccreditationApplicationModel>.Filter.Eq(
                        a => a.LastYearsAccreditationNumber,
                        null
                    ),
                    Builders<AccreditationApplicationModel>.Update.Set(
                        a => a.LastYearsAccreditationNumber,
                        new AccreditationNumberForYearModel
                        {
                            Year = LastYear,
                            AccreditationNumber = plan.RegisterAccreditationNumber!,
                        }
                    ),
                    session,
                    token
                );
                // No content: the guard didn't match, so nothing was written and nothing needs rolling back.
                return updated is null ? Results.NoContent() : Results.Ok();
            },
            cancellationToken
        );

        switch (result)
        {
            case IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK }:
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "RA-784 APPLIED: application {ApplicationId} ({ApplicationReference}, org {OrgId} "
                            + "{OrganisationName}) now holds {Year} accreditation {AccreditationNumber}. Spot "
                            + "check confirmed by {ConfirmedBy}",
                        plan.Id,
                        plan.Application.ApplicationReference,
                        plan.Application.OrgId,
                        plan.Application.OrganisationName,
                        LastYear,
                        plan.RegisterAccreditationNumber,
                        confirmedBy
                    );
                }
                return Write.Written;
            case IStatusCodeHttpResult { StatusCode: StatusCodes.Status204NoContent }:
                logger.LogError(
                    "RA-784 application {ApplicationId} gained last year's accreditation after planning, so "
                        + "nothing was written to it. Re-run the dry run to see its current state.",
                    plan.Id
                );
                return Write.LostTheRace;
            default:
                // DomainModel:DualWrite is on in every environment, so the unit of work catches a failed write,
                // logs why, rolls it back and answers a problem, and the run carries on with the next application.
                logger.LogError(
                    "RA-784 application {ApplicationId}: the write failed and was rolled back, so nothing "
                        + "was written to it. Re-run the dry run to see its current state.",
                    plan.Id
                );
                return Write.Failed;
        }
    }

    // The end-of-run report, part 1: every application whose registration number isn't on the register, with the org
    // and registration number it holds, so each can be checked by hand.
    private void ReportNotOnTheRegister(IReadOnlyList<Plan> plans, string mode)
    {
        var missing = plans
            .Where(p => p.Outcome is Outcome.NotOnTheRegister or Outcome.RegistrationNumberIncomplete)
            .ToList();
        if (!logger.IsEnabled(LogLevel.Warning))
            return;

        logger.LogWarning(
            "RA-784 {Mode} report: {Count} 2027 applications whose registration number is not on the register",
            mode,
            missing.Count
        );
        foreach (var plan in missing)
        {
            logger.LogWarning(
                "RA-784 {Mode} report: not on the register: org {OrgId} {OrganisationName}, registration "
                    + "{RegistrationReference} (previous {PreviousRegistrationNumbers}), application "
                    + "{ApplicationId} ({ApplicationReference})",
                mode,
                plan.Application.OrgId,
                plan.Application.OrganisationName,
                plan.Application.RegistrationReference,
                string.Join(", ", plan.Application.PreviousRegistrationNumbers),
                plan.Id,
                plan.Application.ApplicationReference
            );
        }
    }

    // The end-of-run report, part 2: every register row no 2027 application matched, so whoever reviews the run can
    // see which 2026 registrations haven't applied for 2027 (or hold a registration number that doesn't match).
    private void ReportUnusedRegisterRows(IReadOnlyList<Plan> plans, string mode)
    {
        var used = plans
            .Select(p => p.RegisterRegistrationNumber)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var unused = PublicRegister2026
            .ByRegistrationNumber.Where(r => !used.Contains(r.Key))
            .OrderBy(r => r.Value.OrgId)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
        if (!logger.IsEnabled(LogLevel.Information))
            return;

        logger.LogInformation(
            "RA-784 {Mode} report: {Count} of {Total} register rows matched no 2027 application "
                + "({Accredited} accredited, {RegisteredOnly} registered only)",
            mode,
            unused.Count,
            PublicRegister2026.ByRegistrationNumber.Count,
            unused.Count(r => r.Value.AccreditationNumber is not null),
            unused.Count(r => r.Value.AccreditationNumber is null)
        );
        foreach (var (registrationNumber, row) in unused)
        {
            logger.LogInformation(
                "RA-784 {Mode} report: register row not used: org {OrgId} {OrganisationName}, registration "
                    + "{RegistrationNumber}, 2026 accreditation {AccreditationNumber}",
                mode,
                row.OrgId,
                row.OrganisationName,
                registrationNumber,
                row.AccreditationNumber ?? "none (registered only)"
            );
        }
    }

    // Planned against actual: the outcomes are what the plan found; the writes are what an APPLY run did with the
    // Added ones (a dry run writes nothing).
    private void ReportSummary(IReadOnlyList<Plan> plans, IReadOnlyList<Write> writes, string mode)
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;

        var written = string.Join(
            ", ",
            Enum.GetValues<Write>().Select(w => $"{w}={writes.Count(x => x == w)}")
        );
        var outcomes = string.Join(
            ", ",
            Enum.GetValues<Outcome>().Select(o => $"{o}={plans.Count(p => p.Outcome == o)}")
        );
        var issued = string.Join(
            ", ",
            Enum.GetValues<IssuedNumber>().Select(i => $"{i}={plans.Count(p => p.Issued == i)}")
        );
        logger.LogInformation(
            "RA-784 last year's accreditation backfill complete. Mode={Mode}, applications={Count}. "
                + "Last year's accreditation planned: {Outcomes}. Written: {Written}. Issued numbers: {Issued}",
            mode,
            plans.Count,
            outcomes,
            written,
            issued
        );
    }
}
