namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// What a service client may narrow the granted accreditation search by. Every member is optional
/// and they combine with AND. Only business references are used, never database ids.
/// </summary>
public sealed record GrantedAccreditationFilter(
    int? Year = null,
    int? OrganisationNumber = null,
    string? RegistrationNumber = null
);
