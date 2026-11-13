namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// RA-763: an accreditation number the registration held for a year, as ReEx issued it. This service holds no
/// Accreditation for those years (D10), so the number is kept on the Registration.
/// </summary>
public class RegistrationAccreditationNumber
{
    /// <summary>The year the accreditation applies to, e.g. 2026.</summary>
    public int Year { get; set; }

    /// <summary>e.g. A26ER5003240164PL.</summary>
    public required string AccreditationNumber { get; set; }
}
