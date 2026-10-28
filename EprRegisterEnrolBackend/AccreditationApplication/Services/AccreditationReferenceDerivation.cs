using System.Text.RegularExpressions;
using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// Derives an accreditation reference from a source reference: the leading character becomes
/// 'A', characters 2-3 become the year applied for (mod 100, zero-padded), and every other
/// character is copied unchanged.
/// </summary>
///
/// <remarks>
/// RA-690: the single derivation rule for accreditation references (also intended for reuse by
/// RA-691's migration). Deliberately I/O-free - the caller chooses the source (the latest
/// previous approved accreditation held in this backend, otherwise the application's
/// registration reference) and supplies the year applied for.
///
/// Source validation is shape only: the source must be a 17-character reference
/// (<c>{R|A}</c> + <c>YY</c> + agency <c>{E|S|W|N}</c> + process type <c>{R|X}</c> + 6-digit
/// OrgId + 4-digit sequence + material <c>{ST|WO|AL|FB|PA|PL|GR|GO}</c>) whose leading letter
/// suits its kind - 'A' for a previous accreditation, 'R' for a registration. Nation, exporter
/// flag, OrgId and material are NOT cross-checked against the application: the existing
/// reference is the authority. The same format is produced by RegulatoryNumberGenerator, so a
/// new agency or material code must be added in both places.
/// </remarks>
public static partial class AccreditationReferenceDerivation
{
    public static AccreditationReferenceDerivationResult Derive(
        string? sourceReference,
        AccreditationReferenceSourceKind sourceKind,
        int yearAppliedFor
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(yearAppliedFor);

        var (expectedPrefix, kindDescription) = sourceKind switch
        {
            AccreditationReferenceSourceKind.PreviousAccreditation => (
                'A',
                "previous accreditation reference"
            ),
            AccreditationReferenceSourceKind.Registration => ('R', "registration reference"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(sourceKind),
                sourceKind,
                "Unrecognised accreditation reference source kind."
            ),
        };

        if (sourceReference is null)
        {
            return AccreditationReferenceDerivationResult.Invalid(
                $"The {kindDescription} is missing, so no accreditation reference can be derived."
            );
        }

        if (!ReferenceFormat().IsMatch(sourceReference) || sourceReference[0] != expectedPrefix)
        {
            return AccreditationReferenceDerivationResult.Invalid(
                $"The {kindDescription} '{sourceReference}' is not a valid "
                    + $"{expectedPrefix}-prefixed 17-character reference, so no accreditation "
                    + "reference can be derived from it."
            );
        }

        return AccreditationReferenceDerivationResult.Valid(
            $"A{yearAppliedFor % 100:D2}{sourceReference[3..]}"
        );
    }

    // [0-9] rather than \d: .NET's \d also matches non-ASCII Unicode digits. Case-sensitive, so
    // lower-case references are rejected. S6444: explicit timeout.
    [GeneratedRegex(
        "^[RA][0-9]{2}[ESWN][RX][0-9]{6}[0-9]{4}(ST|WO|AL|FB|PA|PL|GR|GO)$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 100
    )]
    private static partial Regex ReferenceFormat();
}

public record AccreditationReferenceDerivationResult
{
    private AccreditationReferenceDerivationResult(string? reference, string? error)
    {
        Reference = reference;
        Error = error;
    }

    /// <summary>The derived accreditation reference; null when the source was invalid.</summary>
    public string? Reference { get; }

    /// <summary>Why the source was rejected, naming the source and its kind; null when valid.</summary>
    public string? Error { get; }

    public bool IsValid => Error is null;

    internal static AccreditationReferenceDerivationResult Valid(string reference) =>
        new(reference, error: null);

    internal static AccreditationReferenceDerivationResult Invalid(string error) =>
        new(reference: null, error);
}
