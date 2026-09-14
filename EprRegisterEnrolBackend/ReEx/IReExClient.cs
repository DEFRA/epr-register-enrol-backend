using EprRegisterEnrolBackend.ReEx.Dtos;

namespace EprRegisterEnrolBackend.ReEx;

public interface IReExClient
{
    Task<ReExResult<OrganisationDto>> GetOrganisationsAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    );

    Task<ReExResult<OverseasSitesDto>> GetOverseasSiteAsync(
        string organisationId,
        string registrationId,
        string accreditationId,
        CancellationToken cancellationToken = default
    );

    // ORS-R: the full set of overseas sites tied to the registration, regardless of whether
    // each one is included in the accreditation. Distinct from GetOverseasSiteAsync (ORS-A),
    // which returns only sites tied to a specific accreditation.
    Task<ReExResult<OverseasSitesDto>> GetRegistrationOverseasSitesAsync(
        string organisationId,
        string registrationId,
        CancellationToken cancellationToken = default
    );
}
