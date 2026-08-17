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

    // RA-292 AC03: marks an authority-to-issue contact the operator introduced during this
    // application, so the regulator can spot it on the work item overview. Derived server-side
    // by PrnsAuthoriserMerge on every write of the PRNs section — any value supplied by a client
    // is advisory only and is discarded. Not an operator-facing concept; the operator UI must
    // never render or set it. Absent on documents that predate RA-292, which deserialise to false.
    public bool IsNew { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlannedTonnageBand
{
    UpTo500,
    UpTo5000,
    UpTo10000,
    Over10000
}
