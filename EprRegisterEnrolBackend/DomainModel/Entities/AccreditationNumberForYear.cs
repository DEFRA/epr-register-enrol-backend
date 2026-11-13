namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// RA-763: an accreditation number and the year it applies to, as ReEx issued it. Held on the Registration, one per
/// year, and on an application as last year's, because this service holds no Accreditation for those years (D10).
/// </summary>
public class AccreditationNumberForYear
{
    /// <summary>The year the accreditation applies to, e.g. 2026.</summary>
    public int Year { get; set; }

    /// <summary>e.g. A26ER5003240164PL.</summary>
    public required string AccreditationNumber { get; set; }
}
