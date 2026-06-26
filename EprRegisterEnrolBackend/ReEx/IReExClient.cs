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

    Task<ReExResult<bool>> WriteAccreditationAsync(
        string organisationId,
        ReExWriteAccreditationPayload payload,
        CancellationToken cancellationToken = default
    );
}
