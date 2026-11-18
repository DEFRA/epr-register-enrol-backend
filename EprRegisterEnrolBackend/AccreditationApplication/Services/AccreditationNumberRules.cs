using System.Text.RegularExpressions;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// RA-783: how an accreditation number is built on approval, chosen by the operator's history.
/// </summary>
///
/// <remarks>
/// A number is <c>A</c> + <c>YY</c> + agency <c>{E|S|W|N}</c> + process type <c>{R|X}</c> +
/// 6-digit OrgId + 4-digit sequence + 2-letter material, 17 characters in all.
///
/// <list type="table">
///   <listheader><term>Case</term><description>Number on approval</description></listheader>
///   <item>
///     <term>Continuous renewal (accredited last year)</term>
///     <description>Last year's accreditation number with the year changed.</description>
///   </item>
///   <item>
///     <term>First-time (never accredited)</term>
///     <description>The registration number's agency, process type and OrgId, with a new
///     sequence: <c>0</c> + 3 random digits, never the registration number's own.</description>
///   </item>
///   <item>
///     <term>Renewal after a break (accredited before, not last year)</term>
///     <description>As first-time. The new sequence also differs from the earlier accreditation
///     number's.</description>
///   </item>
/// </list>
///
/// A new sequence also avoids every sequence the caller says the organisation's accreditation
/// numbers use under the same agency, process type and OrgId (its other sites, in any year and any
/// material), so it doesn't take the number another site's renewal will need.
///
/// In every case the material code is the caller's: the application's own (<see
/// cref="RegulatoryReferenceCodes.MaterialCodeFor"/>), so glass becomes GR for remelt or GO for
/// other, even when the number it starts from ends in GL. When a glass application holds no
/// process, <see cref="GlassMaterialCodeFrom"/> takes it from the earlier numbers.
///
/// Deliberately I/O-free: the caller supplies the history and a source of random digits, and
/// checks the result isn't already held by another application. A random number that is held
/// is generated again; a renewal's number that is held is a conflict.
/// </remarks>
public static partial class AccreditationNumberRules
{
    // Tries for a random sequence that isn't excluded. The draws are cheap (no I/O), unlike the
    // endpoint's retries, which re-read the holders. An organisation usually excludes a handful of
    // the 1,000 values; running out means it already uses most of them, and comes back as
    // SequencesExhausted, which the endpoint returns as a conflict.
    internal const int MaxSequenceAttempts = 50;

    // Every material code issued. GL is never issued: glass is always GR or GO.
    private const string IssuedMaterialCodes = "ST|WO|AL|FB|PA|PL|GR|GO";

    /// <summary>Which rule applies, from the year of the operator's latest accreditation.</summary>
    public static AccreditationNumberCase CaseFor(int yearAppliedFor, int? latestAccreditedYear) =>
        latestAccreditedYear switch
        {
            null => AccreditationNumberCase.FirstTime,
            { } year when year == yearAppliedFor - 1 => AccreditationNumberCase.ContinuousRenewal,
            _ => AccreditationNumberCase.RenewalAfterBreak,
        };

    /// <param name="yearAppliedFor">The application's year, for example 2027.</param>
    /// <param name="registrationNumber">The registration number, for example R26ER5003240079PL.</param>
    /// <param name="latestAccreditation">The operator's latest accreditation for this
    /// registration and material before <paramref name="yearAppliedFor"/>, or null if never
    /// accredited.</param>
    /// <param name="materialCode">The application's material code: ST, WO, AL, FB, PA, PL, GR or GO.</param>
    /// <param name="randomSequenceDigits">Returns a new sequence's 3 random digits, from 0 to 999.</param>
    /// <param name="organisationAccreditationNumbers">Every accreditation number the
    /// organisation has; a new sequence avoids those under this registration's agency, process
    /// type and OrgId. Malformed ones are skipped: they're held data to avoid, not this number's
    /// source.</param>
    public static AccreditationNumberResult Generate(
        int yearAppliedFor,
        string? registrationNumber,
        PriorAccreditation? latestAccreditation,
        string materialCode,
        Func<int> randomSequenceDigits,
        IEnumerable<string> organisationAccreditationNumbers
    )
    {
        ArgumentNullException.ThrowIfNull(randomSequenceDigits);

        if (!MaterialCodeFormat().IsMatch(materialCode))
            return AccreditationNumberResult.Invalid(
                $"'{materialCode}' is not an accreditation material code."
            );

        // A malformed earlier number fails visibly in every case, rather than being renewed or
        // silently left out of the sequences a new number must avoid.
        if (latestAccreditation is { } prior && InvalidPrior(prior, yearAppliedFor) is { } error)
            return AccreditationNumberResult.Invalid(error);

        var numberCase = CaseFor(yearAppliedFor, latestAccreditation?.Year);
        var year = $"{yearAppliedFor % 100:D2}";

        if (numberCase == AccreditationNumberCase.ContinuousRenewal)
        {
            // Agency, process type, OrgId and sequence kept; year and material replaced.
            var lastYears = latestAccreditation!.AccreditationNumber;
            return AccreditationNumberResult.Valid(
                numberCase,
                $"A{year}{lastYears[AllButYearAndMaterial]}{materialCode}"
            );
        }

        if (!IsReference(registrationNumber, 'R'))
            return AccreditationNumberResult.Invalid(
                $"The registration number '{registrationNumber}' is not a valid "
                    + "R-prefixed 17-character reference."
            );

        var excluded = ExcludedSequences(
            registrationNumber!,
            latestAccreditation,
            organisationAccreditationNumbers
        );
        if (DrawSequence(randomSequenceDigits, excluded) is not { } sequence)
            return AccreditationNumberResult.SequencesExhausted(
                $"Every sequence drawn in {MaxSequenceAttempts} draws is already used by the "
                    + "organisation's accreditation numbers."
            );
        return AccreditationNumberResult.Valid(
            numberCase,
            $"A{year}{registrationNumber![AgencyProcessTypeAndOrgId]}{sequence}{materialCode}"
        );
    }

    /// <summary>
    /// The glass material code an earlier number already carries, for a glass application that
    /// holds no recycling process: GR or GO from the latest accreditation number, else from the
    /// registration number. Null when neither carries one, as 2026 numbers from ReEx end in GL.
    /// </summary>
    public static string? GlassMaterialCodeFrom(
        PriorAccreditation? latestAccreditation,
        string? registrationNumber
    ) =>
        new[] { latestAccreditation?.AccreditationNumber, registrationNumber }
            .Where(number => number is not null && ReferenceFormat().IsMatch(number))
            .Select(number => number![^2..])
            .FirstOrDefault(code => code is "GR" or "GO");

    // Why an earlier accreditation can't be used, or null when it can.
    private static string? InvalidPrior(PriorAccreditation prior, int yearAppliedFor)
    {
        if (prior.Year >= yearAppliedFor)
            return $"{prior.Description} '{prior.AccreditationNumber}' is for {prior.Year}, "
                + $"which isn't before {yearAppliedFor}.";
        if (!IsReference(prior.AccreditationNumber, 'A'))
            return $"{prior.Description} '{prior.AccreditationNumber}' is not a valid "
                + "A-prefixed 17-character reference.";
        return null;
    }

    // The sequences a new number must avoid: the registration's, the earlier accreditation's, and
    // every one the organisation's numbers use under the registration's agency, process type and
    // OrgId.
    private static HashSet<string> ExcludedSequences(
        string registrationNumber,
        PriorAccreditation? latestAccreditation,
        IEnumerable<string>? organisationAccreditationNumbers
    )
    {
        var excluded = new HashSet<string> { registrationNumber[SequenceDigits] };
        if (latestAccreditation is { } earlier)
            excluded.Add(earlier.AccreditationNumber[SequenceDigits]);
        var agencyProcessTypeAndOrgId = registrationNumber[AgencyProcessTypeAndOrgId];
        excluded.UnionWith(
            organisationAccreditationNumbers
                .Where(held =>
                    IsReference(held, 'A')
                    && held[AgencyProcessTypeAndOrgId] == agencyProcessTypeAndOrgId
                )
                .Select(held => held[SequenceDigits])
        );
        return excluded;
    }

    // 0 + 3 random digits, not one of the excluded sequences; null if every draw was excluded.
    private static string? DrawSequence(Func<int> randomSequenceDigits, HashSet<string> excluded)
    {
        for (var attempt = 0; attempt < MaxSequenceAttempts; attempt++)
        {
            var draw = randomSequenceDigits();
            if (draw is < 0 or > 999)
                throw new InvalidOperationException(
                    $"The random source returned {draw}; it must be from 0 to 999."
                );

            var sequence = $"0{draw:D3}";
            if (!excluded.Contains(sequence))
                return sequence;
        }

        return null;
    }

    // Positions in a 17-character reference, A26ER5003240164PL:
    // ER500324 (agency, process type and OrgId), ER5003240164 (all but the prefix, year and
    // material) and 0164 (the sequence).
    private static readonly Range AgencyProcessTypeAndOrgId = 3..11;
    private static readonly Range AllButYearAndMaterial = 3..15;
    private static readonly Range SequenceDigits = 11..15;

    private static bool IsReference(string? reference, char prefix) =>
        reference is not null && ReferenceFormat().IsMatch(reference) && reference[0] == prefix;

    // GL is accepted: 2026 glass numbers from ReEx and the public register end in GL, and the
    // material is replaced anyway. [0-9] rather than \d, which also matches non-ASCII digits.
    // S6444: explicit timeout.
    [GeneratedRegex(
        "^[RA][0-9]{2}[ESWN][RX][0-9]{6}[0-9]{4}(" + IssuedMaterialCodes + "|GL)$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 100
    )]
    private static partial Regex ReferenceFormat();

    [GeneratedRegex(
        "^(" + IssuedMaterialCodes + ")$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 100
    )]
    private static partial Regex MaterialCodeFormat();
}
