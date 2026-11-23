using EprRegisterEnrolBackend.AccreditationApi;
namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

/// <summary>
/// RA-769: the response of the case management accreditation summary read: an explicit allow-list of what an
/// Approved application shows a caseworker. Shaped like the work item payload an application is submitted
/// with (lower-case material, glass process wire value), so the Case Management service reads it with the same code.
/// Never the persistence model, and never the S3 details of a file or the new-site markers.
/// </summary>
public sealed record CaseManagementAccreditationSummary(
    string? OrganisationName,
    int? OrganisationNumber,
    string? AccreditationNumber,
    string? RegistrationNumber,
    int AccreditationYear,
    string? WasteProcessingType,
    string Material,
    GlassRecyclingProcess? GlassRecyclingProcess,
    string? SiteAddress,
    string? CompanyRegisteredAddress,
    CaseManagementSummaryPrns Prns,
    CaseManagementSummaryBusinessPlan BusinessPlan,
    IReadOnlyList<CaseManagementSummarySite> OverseasSites
)
{
    public static CaseManagementAccreditationSummary From(AccreditationApplicationModel application) =>
        new(
            application.OrganisationName,
            application.OrgId,
            application.AccreditationReference,
            application.RegistrationReference,
            application.Year,
            application.WasteProcessingType,
            application.MaterialType.ToString().ToLowerInvariant(),
            application.GlassRecyclingProcess,
            application.SiteAddress,
            application.CompanyRegisteredAddress,
            CaseManagementSummaryPrns.From(application.Prns),
            CaseManagementSummaryBusinessPlan.From(application.BusinessPlan),
            CaseManagementSummarySite.AccreditedFrom(application)
        );
}

/// <summary>
/// RA-769: the PRN details of the summary, shaped like the work item payload's <c>prns</c>: the tonnage band and the
/// authorisers. Never the section status or the version history.
/// </summary>
public sealed record CaseManagementSummaryPrns(
    PlannedTonnageBand? PlannedTonnageBand,
    IReadOnlyList<CaseManagementSummaryAuthoriser> Authorisers
)
{
    public static CaseManagementSummaryPrns From(AccreditationApplicationPrns prns) =>
        new(
            prns.PlannedTonnageBand,
            [.. prns.Authorisers.Select(a => new CaseManagementSummaryAuthoriser(a.FullName, a.Email))]
        );
}

/// <summary>
/// RA-769: an authoriser by name and email only. The per-contact <c>IsNew</c> marker is not carried: it is true for
/// the whole year after the application is granted, so it would mark every contact as new.
/// </summary>
public sealed record CaseManagementSummaryAuthoriser(string FullName, string Email);

/// <summary>
/// RA-769: the business plan of the summary, shaped like the work item payload's <c>businessPlan</c>: each
/// category's share of PRN income and its narrative. Never the section status or the version history.
/// </summary>
public sealed record CaseManagementSummaryBusinessPlan(
    int? NewInfrastructurePercent,
    int? PriceSupportPercent,
    int? BusinessCollectionsPercent,
    int? CommunicationsPercent,
    int? NewMarketsPercent,
    int? NewUsesPercent,
    int? OtherPercent,
    string? NewInfrastructureDetail,
    string? PriceSupportDetail,
    string? BusinessCollectionsDetail,
    string? CommunicationsDetail,
    string? NewMarketsDetail,
    string? NewUsesDetail,
    string? OtherDetail
)
{
    public static CaseManagementSummaryBusinessPlan From(AccreditationApplicationBusinessPlan plan) =>
        new(
            plan.NewInfrastructurePercent,
            plan.PriceSupportPercent,
            plan.BusinessCollectionsPercent,
            plan.CommunicationsPercent,
            plan.NewMarketsPercent,
            plan.NewUsesPercent,
            plan.OtherPercent,
            plan.NewInfrastructureDetail,
            plan.PriceSupportDetail,
            plan.BusinessCollectionsDetail,
            plan.CommunicationsDetail,
            plan.NewMarketsDetail,
            plan.NewUsesDetail,
            plan.OtherDetail
        );
}

/// <summary>
/// RA-769: an overseas reprocessing site of the summary, shaped like the work item payload's site (so Case
/// Management reads it with the code it already has), minus everything that is not a fact about the accredited site:
/// <c>IsNewSite</c> and <c>RegisteredNowAccredited</c> (both stay true for the whole year after grant, so would
/// mark every site as new), <c>Selected</c> (only selected sites are returned) and the ReEx <c>ValidFrom</c>.
/// Interim sites and BES evidence files are added by later tasks.
/// </summary>
public sealed record CaseManagementSummarySite(
    int SiteId,
    string? OrsId,
    string SiteName,
    string? SiteAddress,
    string? AddressLine1,
    string? AddressLine2,
    string? TownOrCity,
    string? Country,
    string? Coordinates,
    string? ContactName,
    string? ContactEmail,
    string? ContactPhone,
    IReadOnlyList<string> OperationCodes,
    string? Code1,
    string? Code2,
    string? Code3,
    string? RepatriatedLoads,
    bool? ConditionsOfExport,
    bool IsEu,
    bool IsOecd
)
{
    /// <summary>
    /// The sites on the accreditation: only those selected (a requested ORS is absent until it is granted),
    /// ordered by ORS id then site id, which is how the Accreditation API orders and defines them.
    /// </summary>
    public static IReadOnlyList<CaseManagementSummarySite> AccreditedFrom(AccreditationApplicationModel application) =>
        [
            .. AccreditationApiMapper
                .AccreditedSites(application)
                .OrderBy(site => site.OrsId, StringComparer.Ordinal)
                .ThenBy(site => site.SiteId)
                .Select(From),
        ];

    private static CaseManagementSummarySite From(OverseasSiteModel site) =>
        new(
            site.SiteId,
            site.OrsId,
            site.SiteName,
            site.SiteAddress,
            site.AddressLine1,
            site.AddressLine2,
            site.TownOrCity,
            site.Country,
            site.Coordinates,
            site.ContactName,
            site.ContactEmail,
            site.ContactPhone,
            [.. site.OperationCodes],
            site.Code1,
            site.Code2,
            site.Code3,
            site.RepatriatedLoads,
            site.ConditionsOfExport,
            site.IsEu,
            site.IsOecd
        );
}
