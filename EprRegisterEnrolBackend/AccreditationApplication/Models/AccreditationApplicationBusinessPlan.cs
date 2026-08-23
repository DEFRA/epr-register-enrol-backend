using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationBusinessPlan
{
    public int? NewInfrastructurePercent { get; set; }
    public int? PriceSupportPercent { get; set; }
    public int? BusinessCollectionsPercent { get; set; }
    public int? CommunicationsPercent { get; set; }
    public int? NewMarketsPercent { get; set; }
    public int? NewUsesPercent { get; set; }
    public int? OtherPercent { get; set; }

    public string? NewInfrastructureDetail { get; set; }
    public string? PriceSupportDetail { get; set; }
    public string? BusinessCollectionsDetail { get; set; }
    public string? CommunicationsDetail { get; set; }
    public string? NewMarketsDetail { get; set; }
    public string? NewUsesDetail { get; set; }
    public string? OtherDetail { get; set; }

    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;

    // Snapshotted on Submit and on each resubmit-after-query — never sent to the FE (RA-311 §6).
    [JsonIgnore]
    public List<BusinessPlanSnapshot> Versions { get; set; } = [];
}

public class BusinessPlanSnapshot
{
    public int? NewInfrastructurePercent { get; set; }
    public int? PriceSupportPercent { get; set; }
    public int? BusinessCollectionsPercent { get; set; }
    public int? CommunicationsPercent { get; set; }
    public int? NewMarketsPercent { get; set; }
    public int? NewUsesPercent { get; set; }
    public int? OtherPercent { get; set; }

    public string? NewInfrastructureDetail { get; set; }
    public string? PriceSupportDetail { get; set; }
    public string? BusinessCollectionsDetail { get; set; }
    public string? CommunicationsDetail { get; set; }
    public string? NewMarketsDetail { get; set; }
    public string? NewUsesDetail { get; set; }
    public string? OtherDetail { get; set; }

    public DateTime VersionedAt { get; set; }
}
