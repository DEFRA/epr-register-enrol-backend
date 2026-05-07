using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

// TODO: implement client
// Stub implementation — swapped for a real HTTP adapter once the ReEx API contract is defined.
public class StubReExApiAdapter(ILogger<StubReExApiAdapter> logger) : IReExApiAdapter
{
    public Task<ReExAccreditationDto?> GetAccreditationAsync(string organisationId, string siteId, MaterialType materialType, int year)
    {
        logger.LogInformation(
            "StubReExApiAdapter.GetAccreditationAsync called for org={OrganisationId} site={SiteId} material={MaterialType} year={Year}",
            organisationId, siteId, materialType, year);

        var fixture = new ReExAccreditationDto
        {
            AccreditationId = $"reex-acc-{organisationId}-{siteId}-{materialType}-{year}",
            OrganisationId = organisationId,
            MaterialType = materialType,
            Year = year,
            SiteId = siteId,
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

        return Task.FromResult<ReExAccreditationDto?>(fixture);
    }

    public Task WriteApprovedAccreditationAsync(ApprovedAccreditationDto accreditation, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "StubReExApiAdapter.WriteApprovedAccreditationAsync called for org={OrganisationId} ref={ApplicationReference}",
            accreditation.OrganisationId, accreditation.ApplicationReference);

        return Task.CompletedTask;
    }
}
