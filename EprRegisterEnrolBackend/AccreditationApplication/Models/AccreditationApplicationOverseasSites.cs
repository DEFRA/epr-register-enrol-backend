namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationOverseasSites
{
    public List<OverseasSiteModel> Sites { get; set; } = [];

    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;
}

public class OverseasSiteModel
{
    public required int SiteId { get; set; }
    public required string SiteName { get; set; }
    public string? SiteAddress { get; set; }
    public string? Country { get; set; }
    public bool IsEu { get; set; }
    public bool IsOecd { get; set; }
    public bool Selected { get; set; } = true;
}
