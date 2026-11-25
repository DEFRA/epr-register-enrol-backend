using EprRegisterEnrolBackend.AccreditationApi;
using EprRegisterEnrolBackend.AccreditationApplication.Services;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

/// <summary>
/// RA-769: the response of the case management accreditation summary read: an explicit allow-list of what an
/// Approved application shows a caseworker. Shaped like the work item payload an application is submitted
/// with (lower-case material, glass process wire value), so the Case Management service reads it with the same code.
/// Never the persistence model, and never the new-site markers. The S3 location of each file (key and bucket) is
/// carried, for the server side only (see <see cref="CaseManagementSummaryFile"/>).
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
    CaseManagementSummaryFiles SamplingPlan,
    IReadOnlyList<CaseManagementSummarySite> OverseasSites
)
{
    public static CaseManagementAccreditationSummary From(AccreditationApplicationModel application) =>
        new(
            OrganisationName: application.OrganisationName,
            OrganisationNumber: application.OrgId,
            AccreditationNumber: application.AccreditationReference,
            RegistrationNumber: application.RegistrationReference,
            AccreditationYear: application.Year,
            WasteProcessingType: AccreditationApiMapper.IsExporter(application) ? "exporter" : "reprocessor",
            Material: application.MaterialType.ToString().ToLowerInvariant(),
            GlassRecyclingProcess: application.GlassRecyclingProcess,
            SiteAddress: application.SiteAddress,
            CompanyRegisteredAddress: application.CompanyRegisteredAddress,
            Prns: CaseManagementSummaryPrns.From(application.Prns),
            BusinessPlan: CaseManagementSummaryBusinessPlan.From(application.BusinessPlan),
            SamplingPlan: CaseManagementSummaryFiles.From(application.SamplingPlan.Files),
            OverseasSites: CaseManagementSummarySite.AccreditedFrom(application)
        );
}

/// <summary>
/// RA-769: the files of a document section (the sampling plan, or a site's BES evidence), shaped like the work item
/// payload's <c>samplingPlan</c> and <c>besEvidence</c> (<c>{ files: [...] }</c>).
/// </summary>
public sealed record CaseManagementSummaryFiles(IReadOnlyList<CaseManagementSummaryFile> Files)
{
    public static CaseManagementSummaryFiles From(IEnumerable<AccreditationApplicationFile> files) =>
        new([.. files.Select(CaseManagementSummaryFile.From)]);

    public static CaseManagementSummaryFiles From(BesEvidenceModel? evidence) =>
        new([.. (evidence?.BesEvidenceUploads ?? []).Select(CaseManagementSummaryFile.From)]);
}

/// <summary>
/// RA-769: one document, with what is needed to serve it from S3 on a download: its id, name, content type, scan
/// result and S3 location (<c>S3Key</c> and <c>S3Bucket</c>). The S3 location is for the server side only:
/// management-be passes it on to management-fe, which serves the file, and it is never rendered to a browser. Who uploaded it, when, the BES validity dates and the document type are not carried. A scan result that
/// is missing (BES evidence can lack one) is answered as <c>Pending</c>: not a clean file.
/// </summary>
public sealed record CaseManagementSummaryFile(
    string FileId,
    string Filename,
    string? ContentType,
    string ScanStatus,
    string S3Key,
    string? S3Bucket
)
{
    private const string Pending = nameof(FileScanStatus.Pending);

    public static CaseManagementSummaryFile From(AccreditationApplicationFile file) =>
        new(
            FileId: file.FileId,
            Filename: file.Filename,
            ContentType: file.ContentType,
            ScanStatus: file.ScanStatus.ToString(),
            S3Key: file.S3Key,
            S3Bucket: file.S3Bucket
        );

    public static CaseManagementSummaryFile From(BesEvidenceFileModel file) =>
        new(
            FileId: file.FileId,
            Filename: file.Filename,
            ContentType: file.ContentType,
            ScanStatus: string.IsNullOrWhiteSpace(file.ScanStatus) ? Pending : file.ScanStatus,
            S3Key: file.S3Key,
            S3Bucket: file.S3Bucket
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
            PlannedTonnageBand: prns.PlannedTonnageBand,
            Authorisers: [.. prns.Authorisers.Select(a => new CaseManagementSummaryAuthoriser(a.FullName, a.Email))]
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
            NewInfrastructurePercent: plan.NewInfrastructurePercent,
            PriceSupportPercent: plan.PriceSupportPercent,
            BusinessCollectionsPercent: plan.BusinessCollectionsPercent,
            CommunicationsPercent: plan.CommunicationsPercent,
            NewMarketsPercent: plan.NewMarketsPercent,
            NewUsesPercent: plan.NewUsesPercent,
            OtherPercent: plan.OtherPercent,
            NewInfrastructureDetail: plan.NewInfrastructureDetail,
            PriceSupportDetail: plan.PriceSupportDetail,
            BusinessCollectionsDetail: plan.BusinessCollectionsDetail,
            CommunicationsDetail: plan.CommunicationsDetail,
            NewMarketsDetail: plan.NewMarketsDetail,
            NewUsesDetail: plan.NewUsesDetail,
            OtherDetail: plan.OtherDetail
        );
}

/// <summary>
/// RA-769: an overseas reprocessing site of the summary, shaped like the work item payload's site (so Case
/// Management reads it with the code it already has), minus everything that is not a fact about the accredited site:
/// <c>IsNewSite</c> and <c>RegisteredNowAccredited</c> (both stay true for the whole year after grant, so would
/// mark every site as new), <c>Selected</c> (only selected sites are returned) and the ReEx <c>ValidFrom</c>.
/// Its interim sites are the active ones only, and its BES evidence is the files alone.
///
/// This is the second mapping of an accredited site, next to <c>AccreditationApiMapper.ToOverseasSite</c>. What is
/// shared: which sites (<c>AccreditationApiMapper.OrderedAccreditedSites</c>), the empty-string-to-null rule
/// (<c>AccreditationApiMapper.Blank</c>) and the exporter rule (<c>AccreditationApiMapper.IsExporter</c>, which
/// answers <see cref="CaseManagementAccreditationSummary.WasteProcessingType"/>). What intentionally differs, for
/// Case Management: only the active interim sites are returned (the Accreditation API returns the withdrawn ones
/// too, with their status history); there is no status or history at all; and the sites are returned for whatever
/// the processing type is (the Accreditation API serves sites for exporters only): the summary shows
/// whatever sites the Approved application holds, and only an exporter is expected to hold any. A change to the shared rules
/// reaches both mappings; a change to a field of one needs the other considered.
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
    bool IsOecd,
    IReadOnlyList<CaseManagementSummaryInterimSite> InterimSites,
    CaseManagementSummaryFiles BesEvidence
)
{
    /// <summary>
    /// The sites on the accreditation: only those selected (a requested ORS is absent until it is granted),
    /// ordered by ORS id then site id, which is how the Accreditation API orders and defines them.
    /// </summary>
    public static IReadOnlyList<CaseManagementSummarySite> AccreditedFrom(AccreditationApplicationModel application) =>
        [
            .. AccreditationApiMapper.OrderedAccreditedSites(application).Select(From),
        ];

    private static CaseManagementSummarySite From(OverseasSiteModel site) =>
        new(
            SiteId: site.SiteId,
            OrsId: site.OrsId,
            SiteName: site.SiteName,
            SiteAddress: AccreditationApiMapper.Blank(site.SiteAddress),
            AddressLine1: AccreditationApiMapper.Blank(site.AddressLine1),
            AddressLine2: AccreditationApiMapper.Blank(site.AddressLine2),
            TownOrCity: AccreditationApiMapper.Blank(site.TownOrCity),
            Country: AccreditationApiMapper.Blank(site.Country),
            Coordinates: AccreditationApiMapper.Blank(site.Coordinates),
            ContactName: AccreditationApiMapper.Blank(site.ContactName),
            ContactEmail: AccreditationApiMapper.Blank(site.ContactEmail),
            ContactPhone: AccreditationApiMapper.Blank(site.ContactPhone),
            OperationCodes: [.. site.OperationCodes],
            Code1: site.Code1,
            Code2: site.Code2,
            Code3: site.Code3,
            RepatriatedLoads: AccreditationApiMapper.Blank(site.RepatriatedLoads),
            ConditionsOfExport: site.ConditionsOfExport,
            IsEu: site.IsEu,
            IsOecd: site.IsOecd,
            InterimSites: [.. InterimSiteSync.Active(site).Select(CaseManagementSummaryInterimSite.From)],
            BesEvidence: CaseManagementSummaryFiles.From(site.BesEvidence)
        );
}

/// <summary>
/// RA-769: an interim site of an overseas site, shaped like the work item payload's interim site. Only active
/// interim sites are ever built (a withdrawn one, with <c>RemovedAt</c> set, is not returned), so neither
/// <c>RemovedAt</c> nor the status history is carried. <c>IsNewSite</c> and <c>CreatedAt</c> are not carried
/// either: the former stays true for the year after grant. The empty-string-to-null rule is the Accreditation API's
/// (<c>AccreditationApiMapper.Blank</c>, on the optional fields it applies it to); unlike the API, which returns
/// withdrawn interim sites with their status history, only the active ones are returned and no status is carried.
/// </summary>
public sealed record CaseManagementSummaryInterimSite(
    int SiteId,
    string SiteNumber,
    string SiteName,
    string AddressLine1,
    string? AddressLine2,
    string TownOrCity,
    string? StateOrRegion,
    string? Postcode,
    string Country,
    string ContactName,
    string ContactEmail,
    string ContactPhone,
    IReadOnlyList<string> OperationCodes
)
{
    public static CaseManagementSummaryInterimSite From(InterimSiteModel interim) =>
        new(
            SiteId: interim.SiteId,
            SiteNumber: interim.SiteNumber,
            SiteName: interim.SiteName,
            AddressLine1: interim.AddressLine1,
            AddressLine2: AccreditationApiMapper.Blank(interim.AddressLine2),
            TownOrCity: interim.TownOrCity,
            StateOrRegion: AccreditationApiMapper.Blank(interim.StateOrRegion),
            Postcode: AccreditationApiMapper.Blank(interim.Postcode),
            Country: interim.Country,
            ContactName: interim.ContactName,
            ContactEmail: interim.ContactEmail,
            ContactPhone: interim.ContactPhone,
            OperationCodes: [.. interim.OperationCodes]
        );
}
