namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

/// <summary>
/// An overseas site as it was submitted, kept in <see cref="OverseasSitesSectionSnapshot"/>. The legacy
/// <c>OverseasSiteModel</c> shape, without its domain model FK, which belongs to the legacy document only.
/// </summary>
public class SubmittedOverseasSite
{
    public required int SiteId { get; set; }
    public string? OrsId { get; set; }
    public required string SiteName { get; set; }
    public string? SiteAddress { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? TownOrCity { get; set; }
    public string? Country { get; set; }
    public string? Coordinates { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public List<string> OperationCodes { get; set; } = [];
    public string? Code1 { get; set; }
    public string? Code2 { get; set; }
    public string? Code3 { get; set; }
    public string? RepatriatedLoads { get; set; }
    public bool? ConditionsOfExport { get; set; }
    public bool IsEu { get; set; }
    public bool IsOecd { get; set; }
    public string? ValidFrom { get; set; }
    public bool Selected { get; set; } = true;
    public BesEvidence? BesEvidence { get; set; }
    public bool IsNewSite { get; set; }
    public bool RegisteredNowAccredited { get; set; }
    public List<SubmittedOverseasSite> PreviousSites { get; set; } = [];
    public SubmittedInterimSite? InterimSite { get; set; }
    public List<SubmittedInterimSite> InterimSites { get; set; } = [];
}
