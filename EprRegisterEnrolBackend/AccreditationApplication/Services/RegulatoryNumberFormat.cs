using EprRegisterEnrolBackend.AccreditationApplication.Models;
using static EprRegisterEnrolBackend.AccreditationApplication.Services.RegulatoryReferenceCodes;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// The registration number format: <c>R</c> + <c>YY</c> + agency + process type + 6-digit OrgId +
/// 4-digit sequence + material code, e.g. <c>R26EX0500180001PL</c>.
/// </summary>
///
/// <remarks>
/// Pure, so <see cref="RegulatoryNumberGenerator"/> and the dev seeds format numbers the same way.
/// <see cref="For"/> resolves and validates every code, so the generator can reject an invalid
/// spec before drawing a sequence number. There is no accreditation number entry point: an
/// accreditation number is derived from the registration number (see
/// <see cref="AccreditationReferenceDerivation"/>), not formatted from a spec.
/// </remarks>
public sealed class RegulatoryNumberFormat
{
    private readonly int _year;
    private readonly int _orgId;
    private readonly string _materialCode;

    private RegulatoryNumberFormat(RegulatoryNumberSpec spec)
    {
        AgencyType = $"{AgencyLetterFor(spec.Nation)}{ProcessTypeLetterFor(spec.IsExporter)}";
        _materialCode = MaterialCodeFor(spec.Material, spec.GlassRecyclingProcess);
        _year = spec.Year;
        _orgId = spec.OrgId;
    }

    /// <summary>Throws when the spec's nation or material cannot be coded.</summary>
    public static RegulatoryNumberFormat For(RegulatoryNumberSpec spec) => new(spec);

    /// <summary>Agency letter + process type letter, e.g. <c>EX</c>.</summary>
    public string AgencyType { get; }

    public string RegistrationNumber(int sequence) =>
        $"R{_year % 100:D2}{AgencyType}{_orgId:D6}{sequence:D4}{_materialCode}";
}
