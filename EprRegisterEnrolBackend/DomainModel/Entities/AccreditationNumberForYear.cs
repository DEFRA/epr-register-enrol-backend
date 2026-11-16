using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// RA-763: an accreditation number and the year it applies to, as ReEx issued it. Held on the Registration, one per
/// year, and on an application as last year's, because this service holds no Accreditation for those years (decision
/// D10 in docs/analysis/register-enrol-domain-model-design.md: the 2026 import is descoped).
/// </summary>
public class AccreditationNumberForYear
{
    /// <summary>The year the accreditation applies to, e.g. 2026.</summary>
    public int Year { get; set; }

    /// <summary>e.g. A26ER5003240164PL.</summary>
    public required string AccreditationNumber { get; set; }

    /// <summary>The legacy application's copy as the domain model's, so both directions of the mapping stay in step.</summary>
    public static AccreditationNumberForYear? FromLegacy(AccreditationNumberForYearModel? legacy) =>
        legacy is null
            ? null
            : new AccreditationNumberForYear
            {
                Year = legacy.Year,
                AccreditationNumber = legacy.AccreditationNumber,
            };

    /// <summary>The domain model's copy as the legacy application's.</summary>
    public static AccreditationNumberForYearModel? ToLegacy(AccreditationNumberForYear? number) =>
        number is null
            ? null
            : new AccreditationNumberForYearModel
            {
                Year = number.Year,
                AccreditationNumber = number.AccreditationNumber,
            };
}