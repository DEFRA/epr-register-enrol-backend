using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApi;

/// <summary>
/// Builds the accreditation API's responses from a granted accreditation application. This
/// backend holds applications, not accreditations of its own, so a granted application is the
/// accreditation. Nothing is invented: what the application does not hold is left out.
/// </summary>
public static class AccreditationApiMapper
{
    public static AccreditationDto ToAccreditation(AccreditationApplicationModel application)
    {
        var exporter = IsExporter(application);
        var businessPlan = BusinessPlan(application.BusinessPlan);

        return new AccreditationDto
        {
            AccreditationReference = application.AccreditationReference!,
            RegistrationNumber = application.RegistrationReference!,
            Year = application.Year,
            Status = "approved",
            Material = application.MaterialType.ToString(),
            GlassRecyclingProcess =
                application.MaterialType == MaterialType.Glass
                    ? application.GlassRecyclingProcess switch
                    {
                        GlassRecyclingProcess.Remelt => "glass_re_melt",
                        GlassRecyclingProcess.Other => "glass_other",
                        _ => null,
                    }
                    : null,
            WasteProcessingType = exporter ? "exporter" : "reprocessor",
            SiteAddress =
                !exporter && !string.IsNullOrWhiteSpace(application.SiteAddress)
                    ? new AddressDto { FullAddress = application.SiteAddress }
                    : null,
            PrnIssuance = new PrnIssuanceDto
            {
                PlannedIssuance = application.Prns.PlannedTonnageBand?.ToString(),
                Signatories = application
                    .Prns.Authorisers.Select(a => new ContactDetailsDto
                    {
                        FullName = a.FullName,
                        Email = a.Email,
                    })
                    .ToList(),
            },
            BusinessPlan = businessPlan.Count == 0 ? null : businessPlan,
            SubmitterContactDetails = ToContact(application.SubmitterContactDetails),
            OverseasSites = exporter
                ? (application.OverseasSites?.Sites ?? [])
                    .Where(site => !string.IsNullOrWhiteSpace(site.OrsId))
                    .Select(site => site.OrsId!)
                    .Distinct()
                    .Order(StringComparer.Ordinal)
                    .ToList()
                : null,
        };
    }

    public static AccreditedOverseasSiteDto ToOverseasSite(OverseasSiteModel site) =>
        new()
        {
            SiteId = site.SiteId,
            OrsId = site.OrsId,
            SiteName = site.SiteName,
            FormattedAddress = Blank(site.SiteAddress),
            Address = ToOverseasAddress(site),
            Coordinates = Blank(site.Coordinates),
            OperationCodes = site.OperationCodes,
            Code1 = Blank(site.Code1),
            Code2 = Blank(site.Code2),
            Code3 = Blank(site.Code3),
            RepatriatedLoads = Blank(site.RepatriatedLoads),
            ConditionsOfExport = site.ConditionsOfExport,
            IsEu = site.IsEu,
            IsOecd = site.IsOecd,
            InterimSiteIds = site
                .InterimSites.Where(interim => interim.RemovedAt is null)
                .Select(interim => interim.SiteId)
                .Order()
                .ToList(),
            ValidFrom = Blank(site.ValidFrom),
            Selected = site.Selected,
            RegisteredNowAccredited = site.RegisteredNowAccredited,
            IsNewSite = site.IsNewSite,
        };

    private static AddressDto? ToOverseasAddress(OverseasSiteModel site)
    {
        var address = new AddressDto
        {
            Line1 = Blank(site.AddressLine1),
            Line2 = Blank(site.AddressLine2),
            Town = Blank(site.TownOrCity),
            Country = Blank(site.Country),
        };

        return address.Line1 is null
            && address.Line2 is null
            && address.Town is null
            && address.Country is null
            ? null
            : address;
    }

    // The seven business plan categories as the application holds them, in the order the
    // operator is asked for them. A category with neither a percentage nor an explanation was
    // not filled in and is left out.
    private static List<BusinessPlanItemDto> BusinessPlan(AccreditationApplicationBusinessPlan plan)
    {
        var categories = new (string Description, int? Percent, string? Detail)[]
        {
            ("New infrastructure", plan.NewInfrastructurePercent, plan.NewInfrastructureDetail),
            ("Price support", plan.PriceSupportPercent, plan.PriceSupportDetail),
            ("Business collections", plan.BusinessCollectionsPercent, plan.BusinessCollectionsDetail),
            ("Communications", plan.CommunicationsPercent, plan.CommunicationsDetail),
            ("New markets", plan.NewMarketsPercent, plan.NewMarketsDetail),
            ("New uses", plan.NewUsesPercent, plan.NewUsesDetail),
            ("Other", plan.OtherPercent, plan.OtherDetail),
        };

        return categories
            .Where(c => c.Percent is not null || !string.IsNullOrWhiteSpace(c.Detail))
            .Select(c => new BusinessPlanItemDto
            {
                Description = c.Description,
                DetailedDescription = Blank(c.Detail),
                PercentSpent = c.Percent,
            })
            .ToList();
    }

    private static ContactDetailsDto? ToContact(SubmitterContactDetailsModel? contact) =>
        contact is null
            ? null
            : new ContactDetailsDto
            {
                FullName = Blank(contact.FullName),
                Email = Blank(contact.Email),
                Phone = Blank(contact.Phone),
                Title = Blank(contact.JobTitle),
            };

    private static bool IsExporter(AccreditationApplicationModel application) =>
        string.IsNullOrWhiteSpace(application.WasteProcessingType)
            ? application.IsExporter
            : string.Equals(
                application.WasteProcessingType,
                "exporter",
                StringComparison.OrdinalIgnoreCase
            );

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
