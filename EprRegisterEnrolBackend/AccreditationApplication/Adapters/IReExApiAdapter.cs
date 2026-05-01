using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public interface IReExApiAdapter
{
    Task<ReExAccreditationDto?> GetAccreditationAsync(string organisationId, MaterialType materialType, int year);
    Task WriteApprovedAccreditationAsync(ApprovedAccreditationDto accreditation, CancellationToken cancellationToken = default);
}

public class ReExAccreditationDto
{
    public string? AccreditationId { get; set; }
    public string? OrganisationId { get; set; }
    public MaterialType MaterialType { get; set; }
    public int Year { get; set; }
    public string? SiteId { get; set; }

    public ReExPrnsDto? Prns { get; set; }
    public ReExBusinessPlanDto? BusinessPlan { get; set; }
}

public class ReExPrnsDto
{
    public PlannedTonnageBand? PlannedTonnageBand { get; set; }
    public List<PrnsAuthoriser> Authorisers { get; set; } = [];
}

public class ReExBusinessPlanDto
{
    public int? NewInfrastructurePercent { get; set; }
    public int? PriceSupportPercent { get; set; }
    public int? BusinessCollectionsPercent { get; set; }
    public int? CommunicationsPercent { get; set; }
    public int? NewMarketsPercent { get; set; }
    public int? NewUsesPercent { get; set; }
}

public class ApprovedAccreditationDto
{
    public required string ApplicationId { get; set; }
    public required string OrganisationId { get; set; }
    public required MaterialType MaterialType { get; set; }
    public required int Year { get; set; }
    public string? SiteId { get; set; }
    public required string ApplicationReference { get; set; }
    public required AccreditationApplicationPrns Prns { get; set; }
    public required AccreditationApplicationBusinessPlan BusinessPlan { get; set; }
}
