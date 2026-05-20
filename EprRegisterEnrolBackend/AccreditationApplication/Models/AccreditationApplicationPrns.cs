using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationPrns
{
    public PlannedTonnageBand? PlannedTonnageBand { get; set; }

    public List<PrnsAuthoriser> Authorisers { get; set; } = [];

    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;
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
