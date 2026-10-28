namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

/// <summary>
/// The business plan's content: how the operator will spend PRN income. Held by an application while it is
/// proposed, and owned by the accreditation once approved, which does not expose it (v2).
/// </summary>
public class BusinessPlan
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
}
