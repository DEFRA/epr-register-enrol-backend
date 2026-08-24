using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.ReEx;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public interface IReExApiAdapter
{
    Task<ReExResult<ReExAccreditationDto>> GetAccreditationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year
    );

    Task<ReExResult<LinkedDefraOrganisationResult>> GetLinkedDefraOrganisationAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// RA-475: the ReEx organisation's own numeric <c>orgId</c> - the value the
    /// 6-digit organisation segment of a regulatory number is defined to be.
    ///
    /// Deliberately NOT <see cref="LinkedDefraOrganisationResult.LinkedDefraOrganisationId"/>,
    /// which is the linked Defra Customer organisation's id and is a UUID (see
    /// LinkedDefraOrganisationDto), and deliberately NOT the <c>{organisationId}</c>
    /// route segment either, which is the ReEx organisation id and is also a UUID.
    /// Neither can be parsed into the number format's <c>{OrgId:D6}</c> segment.
    ///
    /// <c>Value</c> is null when ReEx returned the organisation but it carries no
    /// <c>orgId</c> - a data gap, distinct from a failed lookup (<c>IsSuccess</c> false).
    /// </summary>
    Task<ReExResult<int?>> GetOrganisationNumberAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// The Defra organisation an operator must be related to in order to access the given
/// ReEx organisation. LinkedDefraOrganisationId is null when ReEx has no link recorded.
/// </summary>
public class LinkedDefraOrganisationResult
{
    public required string OrganisationId { get; set; }
    public string? LinkedDefraOrganisationId { get; set; }
}

public class ReExAccreditationDto
{
    public string? AccreditationId { get; set; }
    public string? OrganisationId { get; set; }
    public MaterialType MaterialType { get; set; }
    public int Year { get; set; }
    public string? OrganisationName { get; set; }
    public string? RegistrationReference { get; set; }
    public string? SiteAddress { get; set; }
    public bool IsExporter { get; set; }
    public string? CompanyRegisterAddressPostcode { get; set; }
    public string? CompanyRegisteredAddress { get; set; }
    public string? CompaniesHouseNumber { get; set; }
    public List<string> PermitNumbers { get; set; } = [];
    public string? WasteProcessingType { get; set; }
    public GlassRecyclingProcess? GlassRecyclingProcess { get; set; }
    public List<OverseasSiteModel> OverseasSites { get; set; } = [];

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
    public int? OtherPercent { get; set; }
}
