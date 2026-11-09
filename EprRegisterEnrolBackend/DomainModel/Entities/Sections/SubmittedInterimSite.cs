namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

/// <summary>An interim site as it was submitted (legacy <c>InterimSiteModel</c>).</summary>
public class SubmittedInterimSite
{
    public required int SiteId { get; set; }
    public required string SiteNumber { get; set; }
    public required string Country { get; set; }
    public required string SiteName { get; set; }
    public required string AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public required string TownOrCity { get; set; }
    public string? StateOrRegion { get; set; }
    public string? Postcode { get; set; }
    public required string ContactName { get; set; }
    public required string ContactEmail { get; set; }
    public required string ContactPhone { get; set; }
    public List<string> OperationCodes { get; set; } = [];
    public bool IsNewSite { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? RemovedAt { get; set; }
}
