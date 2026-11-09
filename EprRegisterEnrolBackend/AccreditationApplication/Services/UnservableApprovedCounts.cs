namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// RA-651: how many approved applications, from the first accreditation year on, the accreditation
/// API cannot serve because they lack a business reference. An application can lack more than one,
/// so the counts are per reason and do not add up to a number of applications.
/// </summary>
public sealed record UnservableApprovedCounts(
    int NoOrganisationNumber,
    int NoRegistrationNumber,
    int NoAccreditationNumber
)
{
    public bool Any => NoOrganisationNumber + NoRegistrationNumber + NoAccreditationNumber > 0;
}
