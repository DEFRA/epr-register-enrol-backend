using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Utils;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public class StubReExApiAdapter(
    FakeOrganisationPersistence fakeOrgs,
    ILogger<StubReExApiAdapter> logger
) : IReExApiAdapter
{
    private static readonly (string Country, bool IsEu, bool IsOecd)[] StubSiteData =
    [
        ("Germany", true, true),
        ("France", true, true),
        ("Japan", false, true),
        ("Vietnam", false, false),
    ];

    public async Task<ReExResult<ReExAccreditationDto>> GetAccreditationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year
    )
    {
        logger.LogInformation(
            "StubReExApiAdapter.GetAccreditationAsync called for org={OrganisationId} reg={RegistrationId} material={MaterialType} year={Year}",
            organisationId,
            registrationId,
            materialType,
            year
        );

        string? organisationName = null;
        string? registrationReference = null;
        string? siteAddress = null;
        string? companyRegisterAddressPostcode = null;
        string? companyRegisteredAddress = null;
        string? wasteProcessingType = null;
        var isExporter = false;
        List<OverseasSiteModel> overseasSites = [];

        if (int.TryParse(organisationId, out var orgIdInt))
        {
            var org = await fakeOrgs.GetByOrgIdAsync(orgIdInt);
            organisationName = org?.CompanyDetails?.Name;
            registrationReference = org?.CompanyDetails?.RegistrationNumber;
            companyRegisterAddressPostcode = org?.CompanyDetails?.RegisteredAddress?.Postcode;
            companyRegisteredAddress = org?.CompanyDetails?.RegisteredAddress is { } regAddr
                ? string.Join(
                    ", ",
                    new[]
                    {
                        regAddr.Line1,
                        regAddr.Line2,
                        regAddr.Town,
                        regAddr.County,
                        regAddr.Postcode,
                    }.Where(s => !string.IsNullOrWhiteSpace(s))
                )
                : null;

            var registration = org?.Registrations?.FirstOrDefault(r =>
                r.Id.ToString() == registrationId
            );
            wasteProcessingType = registration?.WasteProcessingType;
            isExporter =
                wasteProcessingType?.Equals("exporter", StringComparison.OrdinalIgnoreCase) == true;
            siteAddress = registration?.SiteAddress is { } addr
                ? $"{addr.Line1}, {addr.Town}, {addr.Postcode}"
                : null;

            if (registration?.OverseasSites is { Count: > 0 } siteIds)
            {
                overseasSites = siteIds
                    .Select(
                        (id, i) =>
                        {
                            var (country, isEu, isOecd) =
                                i < StubSiteData.Length
                                    ? StubSiteData[i]
                                    : ("Unknown", false, false);
                            return new OverseasSiteModel
                            {
                                SiteId = int.TryParse(id, out var parsed) ? parsed : 900001 + i,
                                SiteName = $"Overseas Site {i + 1} ({country})",
                                SiteAddress = $"Address {id}",
                                Country = country,
                                IsEu = isEu,
                                IsOecd = isOecd,
                                IsNewSite = false,
                            };
                        }
                    )
                    .ToList();
            }
        }

        var fixture = new ReExAccreditationDto
        {
            AccreditationId = $"reex-acc-{organisationId}-{materialType}-{year}",
            OrganisationId = organisationId,
            MaterialType = materialType,
            Year = year,
            OrganisationName = organisationName ?? "Stub Reprocessing Ltd",
            RegistrationReference = registrationReference ?? "STUB-REG-001",
            SiteAddress = siteAddress ?? "1 Stub Lane, Stubton, ST1 1AB",
            IsExporter = isExporter,
            CompanyRegisterAddressPostcode = companyRegisterAddressPostcode ?? "ST1 1AB",
            CompanyRegisteredAddress = companyRegisteredAddress ?? "1 Stub Lane, Stubton, ST1 1AB",
            WasteProcessingType = wasteProcessingType ?? (isExporter ? "exporter" : "reprocessor"),
            OverseasSites = overseasSites,
            Prns = new ReExPrnsDto
            {
                PlannedTonnageBand = PlannedTonnageBand.UpTo5000,
                Authorisers =
                [
                    new PrnsAuthoriser { FullName = "Stub Authoriser", Email = "stub@example.com" },
                ],
            },
            BusinessPlan = new ReExBusinessPlanDto
            {
                NewInfrastructurePercent = 20,
                PriceSupportPercent = 20,
                BusinessCollectionsPercent = 20,
                CommunicationsPercent = 20,
                NewMarketsPercent = 10,
                NewUsesPercent = 10,
            },
        };

        return ReExResult<ReExAccreditationDto>.Success(fixture, 200);
    }

    public Task<ReExResult<LinkedDefraOrganisationResult>> GetLinkedDefraOrganisationAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    )
    {
        logger.LogInformation(
            "StubReExApiAdapter.GetLinkedDefraOrganisationAsync called for org={OrganisationId}",
            organisationId
        );

        // Stub: the linked Defra organisation id echoes the ReEx org id so local
        // dev and integration runs are self-consistent.
        return Task.FromResult(
            ReExResult<LinkedDefraOrganisationResult>.Success(
                new LinkedDefraOrganisationResult
                {
                    OrganisationId = organisationId,
                    LinkedDefraOrganisationId = organisationId,
                },
                200
            )
        );
    }
}
