using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.ReEx;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

// Stub implementation — used in Development. Swapped for HttpReExApiAdapter in non-local environments.
public class StubReExApiAdapter(ILogger<StubReExApiAdapter> logger) : IReExApiAdapter
{
    public Task<ReExResult<ReExAccreditationDto>> GetAccreditationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year
    )
    {
        logger.LogInformation(
            "StubReExApiAdapter.GetAccreditationAsync called for org={OrganisationId} reg={RegistrationId} material={MaterialType} year={Year}",
            organisationId, registrationId, materialType, year);

        var fixture = new ReExAccreditationDto
        {
            AccreditationId = $"reex-acc-{organisationId}-{materialType}-{year}",
            OrganisationId = organisationId,
            MaterialType = materialType,
            Year = year,
            OrganisationName = "Stub Reprocessing Ltd",
            RegistrationReference = "STUB-REG-001",
            SiteAddress = "1 Stub Lane, Stubton, ST1 1AB",
            IsExporter = false,
            OverseasSites = [],
            Prns = new ReExPrnsDto
            {
                PlannedTonnageBand = PlannedTonnageBand.UpTo1000,
                Authorisers =
                [
                    new PrnsAuthoriser { FullName = "Stub Authoriser", Email = "stub@example.com" }
                ]
            },
            BusinessPlan = new ReExBusinessPlanDto
            {
                NewInfrastructurePercent = 20,
                PriceSupportPercent = 20,
                BusinessCollectionsPercent = 20,
                CommunicationsPercent = 20,
                NewMarketsPercent = 10,
                NewUsesPercent = 10
            }
        };

        return Task.FromResult(ReExResult<ReExAccreditationDto>.Success(fixture, 200));
    }

    public Task<ReExResult<bool>> WriteApprovedAccreditationAsync(
        ApprovedAccreditationDto accreditation,
        CancellationToken cancellationToken = default
    )
    {
        logger.LogInformation(
            "StubReExApiAdapter.WriteApprovedAccreditationAsync called for org={OrganisationId} ref={ApplicationReference}",
            accreditation.OrganisationId, accreditation.ApplicationReference);

        return Task.FromResult(ReExResult<bool>.Success(true, 200));
    }
}
