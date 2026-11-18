using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// RA-783: issues an accreditation number on approval. It reads the operator's history and the
/// organisation's numbers, has <see cref="AccreditationNumberRules"/> build a number, and checks
/// no other application holds it. Saving the number and logging it are the caller's.
/// </summary>
public sealed class AccreditationNumberIssuer(
    IAccreditationApplicationPersistence persistence,
    IAccreditationSequenceRandom random
)
{
    // RA-783 AC04: a random number already held is drawn again, up to this many times. With
    // about 1,000 sequences per org and material, a second draw is already very unlikely.
    internal const int MaxAttempts = 5;

    /// <param name="application">An open application with no accreditation number yet.</param>
    /// <param name="registrationReference">The application's registration number.</param>
    public async Task<AccreditationNumberIssueOutcome> IssueAsync(
        AccreditationApplicationModel application,
        string registrationReference
    )
    {
        // The operator's latest accreditation before this year, if any:
        // 1. The latest earlier approved accreditation held in this backend (2028 onwards).
        //    Without a registration id there is nothing to match previous years on.
        // 2. RA-763: last year's accreditation number, which Seed keeps from ReEx. 2026
        //    accreditations were granted in ReEx, so this is the source for 2027 renewals.
        // AccreditationNumberRules then decides from its year whether this is a continuous
        // renewal, a renewal after a break, or (with neither) a first-time accreditation.
        var previous = string.IsNullOrEmpty(application.RegistrationId)
            ? null
            : await persistence.GetLatestPreviousAccreditationAsync(
                application.OrganisationId,
                application.RegistrationId,
                application.MaterialType,
                application.Year
            );
        var lastYears = application.LastYearsAccreditationNumber;
        // Last year's number must be for the year before. Seed always stores that, so another
        // year means the data was changed. With nothing held here that fails visibly rather than
        // being treated as a renewal after a break; with an earlier approval held here, that
        // approval is the history and last year's number is ignored (ChooseLatest).
        if (lastYears is not null && lastYears.Year != application.Year - 1 && previous is null)
            return AccreditationNumberIssueOutcome.Failed(
                AccreditationNumberIssueFailure.InvalidHistory,
                $"Last year's accreditation number '{lastYears.AccreditationNumber}' is for "
                    + $"{lastYears.Year}, not {application.Year - 1}, so no accreditation "
                    + "reference can be derived from it."
            );

        var latest = ChooseLatest(previous, lastYears, application.Year);
        var (materialCode, glassProcessUnknown) = MaterialCodeFor(
            application,
            latest,
            registrationReference
        );
        var request = new AccreditationNumberRequest(
            application.Year,
            registrationReference,
            latest,
            materialCode,
            await OrganisationAccreditationNumbersAsync(application, latest)
        );

        var (generated, withdrawnHolderIds, failure) = await GenerateUnheldAsync(
            application,
            request
        );
        if (failure is not null)
            return failure;

        // A renewal names the number it renews; a new number names the registration it's built on.
        var renewal = generated!.Case == AccreditationNumberCase.ContinuousRenewal;
        var sourceKind = renewal ? latest!.Source : AccreditationReferenceSourceKind.Registration;
        return AccreditationNumberIssueOutcome.Success(
            new IssuedAccreditationNumber(
                generated.AccreditationNumber!,
                generated.Case!.Value,
                sourceKind,
                renewal ? latest!.AccreditationNumber : registrationReference,
                sourceKind == AccreditationReferenceSourceKind.PreviousAccreditation
                    ? previous?.ApplicationId
                    : null,
                withdrawnHolderIds,
                glassProcessUnknown
            )
        );
    }

    // The operator's latest accreditation before this year: the later of the approval held here
    // and last year's number from ReEx, the one held here on the same year. Last year's number
    // only counts when it is for the year before; with an approval held here, a wrong-year one is
    // ignored. Null when there is neither, so a first-time accreditation.
    private static PriorAccreditation? ChooseLatest(
        AccreditationApplicationModel? previous,
        AccreditationNumberForYearModel? lastYears,
        int year
    )
    {
        if (
            lastYears is not null
            && lastYears.Year == year - 1
            && (previous is null || lastYears.Year > previous.Year)
        )
            return new PriorAccreditation(
                lastYears.Year,
                lastYears.AccreditationNumber,
                AccreditationReferenceSourceKind.LastYearsAccreditationNumber
            );

        return previous is null
            ? null
            : new PriorAccreditation(
                previous.Year,
                previous.AccreditationReference!,
                AccreditationReferenceSourceKind.PreviousAccreditation
            );
    }

    // RA-783: the material code to issue, and whether glass was issued as other (GO) only because
    // its process is unknown. Glass is GR or GO:
    // 1. The application's recycling process, when it holds one, even if an earlier number
    //    carries the other code.
    // 2. Otherwise GR or GO from the latest accreditation number, then the registration number.
    // 3. Otherwise (2026 ReEx numbers end in GL) other (GO), the only other glass code case
    //    management accepts, so the caller logs an error for someone to confirm the process.
    private static (string MaterialCode, bool GlassProcessUnknown) MaterialCodeFor(
        AccreditationApplicationModel application,
        PriorAccreditation? latest,
        string registrationReference
    )
    {
        if (
            application.MaterialType != MaterialType.Glass
            || application.GlassRecyclingProcess is not null
        )
            return (
                RegulatoryReferenceCodes.MaterialCodeFor(
                    application.MaterialType,
                    application.GlassRecyclingProcess
                ),
                false
            );

        return AccreditationNumberRules.GlassMaterialCodeFrom(latest, registrationReference) is
        { } carried
            ? (carried, false)
            : (
                RegulatoryReferenceCodes.MaterialCodeFor(
                    MaterialType.Glass,
                    GlassRecyclingProcess.Other
                ),
                true
            );
    }

    // RA-783 AC04: a new number's sequence avoids every accreditation number the organisation's
    // applications hold here - issued, replaced, or last year's from ReEx - so a site can't take
    // the number another site's renewal will need. Only data in Mongo is checked, so a 2026
    // number is seen once it's held on one of the organisation's applications (at Seed, or by
    // the RA-763/RA-784 backfills); until then, and for two sites approved at the same moment,
    // a clash makes the renewal's approval a 409, rare enough to put right by hand.
    // The case is checked here only to skip the read: Generate ignores these numbers for a
    // continuous renewal anyway, as its number is fixed.
    private async Task<IReadOnlyCollection<string>> OrganisationAccreditationNumbersAsync(
        AccreditationApplicationModel application,
        PriorAccreditation? latest
    ) =>
        AccreditationNumberRules.CaseFor(application.Year, latest?.Year)
        == AccreditationNumberCase.ContinuousRenewal
            ? []
            : (await persistence.GetByOrganisationAsync(application.OrganisationId))
                .SelectMany(HeldAccreditationNumbers)
                .ToList();

    // Every accreditation number an application holds: its issued one, the ones it replaced, and
    // last year's from ReEx.
    private static IEnumerable<string> HeldAccreditationNumbers(
        AccreditationApplicationModel application
    )
    {
        if (application.AccreditationReference is { } issued)
            yield return issued;
        foreach (var replaced in application.PreviousAccreditationNumbers ?? [])
            yield return replaced;
        if (application.LastYearsAccreditationNumber?.AccreditationNumber is { } lastYears)
            yield return lastYears;
    }

    // RA-783 AC04: builds a number no other application holds. The organisation's own numbers are
    // already avoided by the draw; this catches the rest, such as a duplicate live application of
    // another organisation, or a number written after the organisation's were read. Read-then-write:
    // a unique index is a follow-up to RA-691.
    // - A continuous renewal's number is fixed, so a live holder is a conflict. A withdrawn holder
    //   doesn't block: an operator who withdrew and started again renews to the same number, and
    //   the withdrawn record keeps it.
    // - A new random number is drawn again on any holder, withdrawn included, so it is never
    //   knowingly issued twice.
    private async Task<(
        AccreditationNumberResult? Generated,
        IReadOnlyList<MongoDB.Bson.ObjectId> WithdrawnHolderIds,
        AccreditationNumberIssueOutcome? Failure
    )> GenerateUnheldAsync(AccreditationApplicationModel application, AccreditationNumberRequest request)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var generated = AccreditationNumberRules.Generate(request, random.NextSequenceDigits);
            // The organisation already uses nearly every sequence: the same conflict as every
            // number drawn being held.
            if (generated.NoSequenceLeft)
                break;
            // A malformed source fails visibly rather than issuing a wrong number.
            if (!generated.IsValid)
                return (
                    null,
                    [],
                    AccreditationNumberIssueOutcome.Failed(
                        AccreditationNumberIssueFailure.InvalidHistory,
                        generated.Error!
                    )
                );

            var holders = await persistence.GetOtherAccreditationReferenceHoldersAsync(
                generated.AccreditationNumber!,
                application.Id!.Value
            );

            if (generated.Case == AccreditationNumberCase.ContinuousRenewal)
                return holders.Any(h => h.BlocksIssuing)
                    ? (
                        null,
                        [],
                        AccreditationNumberIssueOutcome.Failed(
                            AccreditationNumberIssueFailure.AlreadyHeld,
                            $"Accreditation reference {generated.AccreditationNumber} is already "
                                + "held by another application; it cannot be issued again."
                        )
                    )
                    : (generated, [.. holders.Select(h => h.Id)], null);

            if (holders.Count == 0)
                return (generated, [], null);
        }

        return (
            null,
            [],
            AccreditationNumberIssueOutcome.Failed(
                AccreditationNumberIssueFailure.AlreadyHeld,
                $"No accreditation reference that isn't already held was found in {MaxAttempts} "
                    + "attempts."
            )
        );
    }
}
