namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

public class SamplingPlanSectionSnapshot
{
    public List<ApplicationFile> Files { get; set; } = [];
    public DateTime VersionedAt { get; set; }
}
