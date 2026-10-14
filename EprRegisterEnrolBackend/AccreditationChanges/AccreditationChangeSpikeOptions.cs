using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationChanges;

public class AccreditationChangeSpikeOptions
{
    public bool Enabled { get; set; }
    public string OrganisationId { get; set; } = "";
    public string RegistrationId { get; set; } = "";
    public string AccreditationId { get; set; } = "";
    public MaterialType MaterialType { get; set; }
    public int Year { get; set; }
    public PreparedOverseasSite Proposal { get; set; } = new();
}

public class PreparedOverseasSite
{
    public string SiteName { get; set; } = "";
    public string SiteAddress { get; set; } = "";
    public string Country { get; set; } = "";
}