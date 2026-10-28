namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

public class OverseasSitesSectionSnapshot
{
    public List<SubmittedOverseasSite> Sites { get; set; } = [];
    public DateTime VersionedAt { get; set; }
}
