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
///     sequence: <c>0</c> + 3 random digits.</description>
///   </item>
///   <item>
///     <term>Renewal after a break (accredited before, not last year)</term>
///     <description>As first-time. The new sequence differs from the registration number's and
///     the earlier accreditation number's.</description>
///   </item>
/// </list>
///
/// In every case the material code is the application's own (<see
/// cref="RegulatoryReferenceCodes.MaterialCodeFor"/>), so glass becomes GR for remelt or GO for
/// other, even when the number it starts from ends in GL.
///
/// Deliberately I/O-free: the caller supplies the history and a source of random digits, and
/// checks the result isn't already held by another application. A random number that is held
/// is generated again; a renewal's number that is held is a conflict.
/// </remarks>
public static partial class AccreditationNumberRules
{
    // Tries for a random sequence that differs from the registration's and the earlier
    // accreditation's. Only two of the 1,000 values are excluded, so this is never reached
    // with a real random source; it only stops a stuck source looping forever.
    internal const int MaxSequenceAttempts = 20;

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
    /// <param name="randomBelow1000">Returns a random number from 0 to 999.</param>
    public static AccreditationNumberResult Generate(
        int yearAppliedFor,
        string? registrationNumber,
        PriorAccreditation? latestAccreditation,
        string materialCode,
        Func<int> randomBelow1000
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(yearAppliedFor);
        ArgumentNullException.ThrowIfNull(randomBelow1000);

        if (!MaterialCodeFormat().IsMatch(materialCode))
            return AccreditationNumberResult.Invalid(
                $"'{materialCode}' is not an accreditation material code."
            );

        if (latestAccreditation is { } prior && prior.Year >= yearAppliedFor)
            return AccreditationNumberResult.Invalid(
                $"The earlier accreditation '{prior.AccreditationNumber}' is for {prior.Year}, "
                    + $"which isn't before {yearAppliedFor}."
            );

        var numberCase = CaseFor(yearAppliedFor, latestAccreditation?.Year);
        var year = $"{yearAppliedFor % 100:D2}";

        if (numberCase == AccreditationNumberCase.ContinuousRenewal)
        {
            var lastYears = latestAccreditation!.AccreditationNumber;
            if (!IsReference(lastYears, 'A'))
                return AccreditationNumberResult.Invalid(
                    $"Last year's accreditation number '{lastYears}' is not a valid "
                        + "A-prefixed 17-character reference."
                );

            // Agency, process type, OrgId and sequence kept; year and material replaced.
            return AccreditationNumberResult.Valid(
                numberCase,
                $"A{year}{lastYears![AgencyToSequence]}{materialCode}"
            );
        }

        if (!IsReference(registrationNumber, 'R'))
            return AccreditationNumberResult.Invalid(
                $"The registration number '{registrationNumber}' is not a valid "
                    + "R-prefixed 17-character reference."
            );

        var excluded = new HashSet<string> { registrationNumber![Sequence] };
        if (
            latestAccreditation?.AccreditationNumber is { } earlier
            && IsReference(earlier, 'A')
        )
            excluded.Add(earlier[Sequence]);

        for (var attempt = 0; attempt < MaxSequenceAttempts; attempt++)
        {
            var draw = randomBelow1000();
            if (draw is < 0 or > 999)
                throw new InvalidOperationException(
                    $"The random source returned {draw}; it must be from 0 to 999."
                );

            var sequence = $"0{draw:D3}";
            if (excluded.Contains(sequence))
                continue;

            return AccreditationNumberResult.Valid(
                numberCase,
                $"A{year}{registrationNumber[AgencyToOrg]}{sequence}{materialCode}"
            );
        }

        return AccreditationNumberResult.Invalid(
            $"No new sequence differing from {string.Join(" and ", excluded)} was found in "
                + $"{MaxSequenceAttempts} attempts."
        );
    }

    // Positions in a 17-character reference.
    private static readonly Range AgencyToOrg = 3..11;
    private static readonly Range AgencyToSequence = 3..15;
    private static readonly Range Sequence = 11..15;

    private static bool IsReference(string? reference, char prefix) =>
        reference is not null && ReferenceFormat().IsMatch(reference) && reference[0] == prefix;

    // As AccreditationReferenceDerivation, but GL is accepted: 2026 glass numbers from ReEx and
    // the public register end in GL, and the material is replaced anyway. [0-9] rather than \d,
    // which also matches non-ASCII digits. S6444: explicit timeout.
    [GeneratedRegex(
        "^[RA][0-9]{2}[ESWN][RX][0-9]{6}[0-9]{4}(ST|WO|AL|FB|PA|PL|GR|GO|GL)$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 100
    )]
    private static partial Regex ReferenceFormat();

    // GL is never issued: glass is always GR or GO.
    [GeneratedRegex("^(ST|WO|AL|FB|PA|PL|GR|GO)$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex MaterialCodeFormat();
}

public enum AccreditationNumberCase
{
    ContinuousRenewal,
    FirstTime,
    RenewalAfterBreak,
}

/// <summary>An earlier accreditation: its year and number, for example 2026, A26ER5003240164PL.</summary>
public sealed record PriorAccreditation(int Year, string AccreditationNumber);

public sealed record AccreditationNumberResult
{
    private AccreditationNumberResult(
        AccreditationNumberCase? numberCase,
        string? accreditationNumber,
        string? error
    )
    {
        Case = numberCase;
        AccreditationNumber = accreditationNumber;
        Error = error;
    }

    /// <summary>Which rule built the number; null when it couldn't be built.</summary>
    public AccreditationNumberCase? Case { get; }

    /// <summary>The number; null when it couldn't be built.</summary>
    public string? AccreditationNumber { get; }

    /// <summary>Why the number couldn't be built; null when it was.</summary>
    public string? Error { get; }

    public bool IsValid => Error is null;

    internal static AccreditationNumberResult Valid(
        AccreditationNumberCase numberCase,
        string accreditationNumber
    ) => new(numberCase, accreditationNumber, error: null);

    internal static AccreditationNumberResult Invalid(string error) =>
        new(numberCase: null, accreditationNumber: null, error);
}
