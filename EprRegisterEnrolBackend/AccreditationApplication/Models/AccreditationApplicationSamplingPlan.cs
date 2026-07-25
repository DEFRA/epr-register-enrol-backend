using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationSamplingPlan
{
    public List<AccreditationApplicationFile> Files { get; set; } = [];

    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;

    // Snapshotted on Submit and on each resubmit-after-query — never sent to the FE (RA-311 §6).
    [JsonIgnore]
    public List<SamplingPlanSnapshot> Versions { get; set; } = [];
}

public class SamplingPlanSnapshot
{
    public List<AccreditationApplicationFile> Files { get; set; } = [];
    public DateTime VersionedAt { get; set; }
}
