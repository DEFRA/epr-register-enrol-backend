namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>RA-783: what <see cref="AccreditationNumberRules.Generate"/> builds a number from.</summary>
/// <param name="YearAppliedFor">The application's year, for example 2027.</param>
/// <param name="RegistrationNumber">The registration number, for example R26ER5003240079PL.</param>
/// <param name="LatestAccreditation">The operator's latest accreditation for this registration
/// and material before <paramref name="YearAppliedFor"/>, or null if never accredited.</param>
/// <param name="MaterialCode">The material code to issue: ST, WO, AL, FB, PA, PL, GR or GO.</param>
/// <param name="OrganisationAccreditationNumbers">Every accreditation number the organisation
/// has; a new sequence avoids those under this registration's agency, process type and OrgId.
/// Malformed ones are skipped: they're held data to avoid, not this number's source.</param>
public sealed record AccreditationNumberRequest(
    int YearAppliedFor,
    string? RegistrationNumber,
    PriorAccreditation? LatestAccreditation,
    string MaterialCode,
    IReadOnlyCollection<string> OrganisationAccreditationNumbers
);
