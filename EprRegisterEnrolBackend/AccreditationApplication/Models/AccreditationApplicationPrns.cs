using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationPrns
{
    public PlannedTonnageBand? PlannedTonnageBand { get; set; }

    public List<PrnsAuthoriser> Authorisers { get; set; } = [];

    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;

    // Snapshotted on Submit and on each resubmit-after-query — never sent to the FE (RA-311 §6).
    [JsonIgnore]
    public List<PrnsSnapshot> Versions { get; set; } = [];
}

public class PrnsSnapshot
{
    public PlannedTonnageBand? PlannedTonnageBand { get; set; }
    public List<PrnsAuthoriser> Authorisers { get; set; } = [];
    public DateTime VersionedAt { get; set; }
}

public class PrnsAuthoriser
{
    public required string FullName { get; set; }
    public required string Email { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlannedTonnageBand
{
    UpTo500,
    UpTo1000,
    UpTo10000,
    Over10000
}
