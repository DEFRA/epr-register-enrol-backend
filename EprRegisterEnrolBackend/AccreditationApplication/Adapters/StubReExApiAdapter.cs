using System.Globalization;
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

    // Mirrors HttpReExApiAdapter.GlassRecyclingProcessMap — the stub reads the
    // same wire-value string from FakeOrganisationPersistence's seed data that
    // the real ReEx API would return, so local dev/e2e exercises the same
    // mapping the production adapter does.
    private static readonly Dictionary<string, GlassRecyclingProcess> GlassRecyclingProcessMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["glass_re_melt"] = GlassRecyclingProcess.Remelt,
            ["glass_other"] = GlassRecyclingProcess.Other,
        };

    public async Task<ReExResult<ReExAccreditationDto>> GetAccreditationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year
    )
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubReExApiAdapter.GetAccreditationAsync called for org={OrganisationId} reg={RegistrationId} material={MaterialType} year={Year}",
                organisationId,
                registrationId,
                materialType,
                year
            );
        }

        string? organisationName = null;
        string? registrationReference = null;
        string? siteAddress = null;
        string? companyRegisterAddressPostcode = null;
        string? companyRegisteredAddress = null;
        string? companiesHouseNumber = null;
        List<string> permitNumbers = [];
        string? wasteProcessingType = null;
        var isExporter = false;
        GlassRecyclingProcess? glassRecyclingProcess = null;
        List<OverseasSiteModel> overseasSites = [];

        if (int.TryParse(organisationId, out var orgIdInt))
        {
            var org = await fakeOrgs.GetByOrgIdAsync(orgIdInt);
            organisationName = org?.CompanyDetails?.Name;
            registrationReference = org?.CompanyDetails?.RegistrationNumber;
            companyRegisterAddressPostcode = org?.CompanyDetails?.RegisteredAddress?.Postcode;
            companiesHouseNumber = org?.CompanyDetails?.CompaniesHouseNumber;
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
            if (
                registration?.GlassRecyclingProcess is { } rawGlassRecyclingProcess
                && GlassRecyclingProcessMap.TryGetValue(
                    rawGlassRecyclingProcess,
                    out var mappedGlassRecyclingProcess
                )
            )
                glassRecyclingProcess = mappedGlassRecyclingProcess;
            isExporter =
                wasteProcessingType?.Equals("exporter", StringComparison.OrdinalIgnoreCase) == true;
            // Mirrors HttpReExApiAdapter: only reprocessors have a UK processing
            // site, so exporters always get a null SiteAddress from the real API.
            siteAddress = isExporter
                ? null
                : registration?.SiteAddress is { } addr
                    ? $"{addr.Line1}, {addr.Town}, {addr.Postcode}"
                    : "1 Stub Lane, Stubton, ST1 1AB";

            permitNumbers = (registration?.WasteManagementPermits ?? [])
                .Select(p => p.PermitNumber)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .ToList();

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
            SiteAddress = siteAddress,
            IsExporter = isExporter,
            CompanyRegisterAddressPostcode = companyRegisterAddressPostcode ?? "ST1 1AB",
            CompanyRegisteredAddress = companyRegisteredAddress
                ?? "1 Stub Registered Office, Stubton, ST1 1AB",
            CompaniesHouseNumber = companiesHouseNumber ?? "00000001",
            PermitNumbers = permitNumbers,
            WasteProcessingType = wasteProcessingType ?? (isExporter ? "exporter" : "reprocessor"),
            GlassRecyclingProcess = glassRecyclingProcess,
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
                BusinessCollectionsPercent = 15,
                CommunicationsPercent = 15,
                NewMarketsPercent = 10,
                NewUsesPercent = 10,
                OtherPercent = 10,
            },
        };

        return ReExResult<ReExAccreditationDto>.Success(fixture, 200);
    }

    public Task<ReExResult<LinkedDefraOrganisationResult>> GetLinkedDefraOrganisationAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    )
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubReExApiAdapter.GetLinkedDefraOrganisationAsync called for org={OrganisationId}",
                organisationId
            );
        }

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

    public Task<ReExResult<int?>> GetOrganisationNumberAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    )
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubReExApiAdapter.GetOrganisationNumberAsync called for org={OrganisationId}",
                organisationId
            );
        }

        // Stub: a numeric org id passes straight through so a locally-seeded
        // numeric organisation behaves exactly as the real ReEx would. Anything
        // else (the UUID a real ReEx organisation id actually is) reports "no
        // orgId recorded" rather than inventing one - the caller then falls back
        // to its own supplied value, which is what every existing local/dev
        // fixture relies on.
        var orgNumber = int.TryParse(
            organisationId,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed
        )
            ? parsed
            : (int?)null;

        return Task.FromResult(ReExResult<int?>.Success(orgNumber, 200));
    }
}
