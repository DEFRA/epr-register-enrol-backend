namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationSamplingPlan
{
    public List<AccreditationApplicationFile> Files { get; set; } = [];

    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;
}
