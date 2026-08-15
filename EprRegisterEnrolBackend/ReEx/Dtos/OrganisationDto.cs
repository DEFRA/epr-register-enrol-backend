using System.Text.Json;
using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.ReEx.Dtos;

public class OrganisationDto
{
    public string? Id { get; init; }
    public int SchemaVersion { get; init; }
    public int? OrgId { get; init; }
    public JsonElement? FormSubmission { get; init; }
    public List<string> WasteProcessingTypes { get; init; } = [];
    public List<string> ReprocessingNations { get; init; } = [];
    public string? BusinessType { get; init; }
    public CompanyDetailsDto? CompanyDetails { get; init; }
    public ContactDetailsDto? SubmitterContactDetails { get; init; }
    public ContactDetailsDto? ManagementContactDetails { get; init; }
    public string? SubmittedToRegulator { get; init; }
    public LinkedDefraOrganisationDto? LinkedDefraOrganisation { get; init; }
    public List<RegistrationBaseDto> Registrations { get; init; } = [];
    public List<AccreditationDto> Accreditations { get; init; } = [];
}

/// <summary>
/// The Defra Customer organisation that this ReEx organisation is linked to.
/// OrgId is the Defra organisation id (a UUID) carried in an operator's Defra ID
/// relationships, used to authorise access to this organisation's data.
/// </summary>
public class LinkedDefraOrganisationDto
{
    public string? OrgId { get; init; }
}

public class CompanyDetailsDto
{
    public string? Name { get; init; }
    public string? TradingName { get; init; }
    public string? RegistrationNumber { get; init; }
    public string? CompaniesHouseNumber { get; init; }
    public RegisteredAddressDto? Address { get; init; }
}

public class RegisteredAddressDto
{
    public string? Line1 { get; init; }
    public string? Line2 { get; init; }
    public string? Town { get; init; }
    public string? County { get; init; }
    public string? Country { get; init; }
    public string? Postcode { get; init; }
}

public class ContactDetailsDto
{
    public string? FullName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? JobTitle { get; init; }
}

/// <summary>
/// Base registration DTO. Polymorphic on wasteProcessingType: "reprocessor" → ReprocessorRegistrationDto,
/// "exporter" → ExporterRegistrationDto. Unknown values fall back to this base class.
/// </summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "wasteProcessingType",
    IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor
)]
[JsonDerivedType(typeof(ReprocessorRegistrationDto), "reprocessor")]
[JsonDerivedType(typeof(ExporterRegistrationDto), "exporter")]
public class RegistrationBaseDto
{
    public string? Id { get; init; }
    public string? AccreditationId { get; init; }
    public string? RegistrationNumber { get; init; }
    public NoticeAddressDto? NoticeAddress { get; init; }
    public List<WasteManagementPermitDto> WasteManagementPermits { get; init; } = [];

    // Present only when wasteProcessingType is glass (values: "glass_re_melt", "glass_other")
    public List<string> GlassRecyclingProcess { get; init; } = [];
}

public class ReprocessorRegistrationDto : RegistrationBaseDto
{
    public SiteDto? Site { get; init; }
    public List<YearlyMetricDto> YearlyMetrics { get; init; } = [];
    public JsonElement? PlantEquipmentDetails { get; init; }
    public string? ReprocessingType { get; init; }
}

public class ExporterRegistrationDto : RegistrationBaseDto
{
    public List<string> ExportPorts { get; init; } = [];
    public List<FileUploadReferenceDto> OrsFileUploads { get; init; } = [];

    // Keyed by a numeric-string site key matching the overseas-sites endpoint
    public Dictionary<string, OverseasSiteRefDto> OverseasSites { get; init; } = [];
}

// ── Site (reprocessor) ───────────────────────────────────────────────────────

public class SiteDto
{
    public SiteAddressDto? Address { get; init; }
    public string? GridReference { get; init; }
    public string? WasteRegistrationNumber { get; init; }
}

public class SiteAddressDto
{
    public string? Line1 { get; init; }
    public string? Line2 { get; init; }
    public string? Town { get; init; }
    public string? County { get; init; }
    public string? Country { get; init; }
    public string? Postcode { get; init; }
}

public class YearlyMetricDto
{
    public int? Year { get; init; }
    public string? Metric { get; init; }
    public MetricInputDto? Input { get; init; }
    public MetricOutputDto? Output { get; init; }
}

public class MetricInputDto
{
    public string? Type { get; init; }

    [JsonPropertyName("ukPackagingWasteInTonnes")]
    public int? UkPackagingWaste { get; init; }

    [JsonPropertyName("nonUkPackagingWasteInTonnes")]
    public int? NonUkPackagingWaste { get; init; }

    [JsonPropertyName("nonPackagingWasteInTonnes")]
    public int? NonPackagingWaste { get; init; }
}

public class MetricOutputDto
{
    public string? Type { get; init; }

    [JsonPropertyName("sentToAnotherSiteInTonnes")]
    public int? SentToAnotherSite { get; init; }

    [JsonPropertyName("contaminantsInTonnes")]
    public int? Contaminants { get; init; }

    [JsonPropertyName("processLossInTonnes")]
    public int? ProcessLoss { get; init; }
}

// ── Shared address / permit types ────────────────────────────────────────────

/// <summary>
/// Polymorphic notice address. Reprocessor shape: Line1/Postcode/Town/Country.
/// Exporter shape: FullAddress/Country. All fields nullable; only the relevant set is populated.
/// </summary>
public class NoticeAddressDto
{
    public string? Line1 { get; init; }
    public string? Postcode { get; init; }
    public string? Town { get; init; }
    public string? FullAddress { get; init; }
    public string? Country { get; init; }
}

/// <summary>
/// Polymorphic permit. Standard permit: Type/PermitNumber/AuthorisedMaterials.
/// Exemption: Type/Exemptions. Exporter: Type only.
/// </summary>
public class WasteManagementPermitDto
{
    public string? Type { get; init; }
    public string? PermitNumber { get; init; }
    public List<AuthorisedMaterialDto> AuthorisedMaterials { get; init; } = [];
    public List<ExemptionDto> Exemptions { get; init; } = [];
}

public class AuthorisedMaterialDto
{
    public string? Material { get; init; }
    public int? AuthorisedWeightInTonnes { get; init; }
    public string? TimeScale { get; init; }
}

public class ExemptionDto
{
    public string? Reference { get; init; }
    public string? ExemptionCode { get; init; }
}

// ── File uploads (exporter) ──────────────────────────────────────────────────

public class FileUploadReferenceDto
{
    public string? DefraFormUploadedFileId { get; init; }
    public string? DefraFormUserDownloadLink { get; init; }
}

/// <summary>Registration-level overseas-site reference (key → overseasSiteId).</summary>
public class OverseasSiteRefDto
{
    public string? OverseasSiteId { get; init; }
}

// ── Accreditation ────────────────────────────────────────────────────────────

public class AccreditationDto
{
    public string? Id { get; init; }
    public string? AccreditationNumber { get; init; }
    public string? Status { get; init; }
    public string? Material { get; init; }
    public string? ValidFrom { get; init; }
    public string? ValidTo { get; init; }
    public PrnIssuanceDto? PrnIssuance { get; init; }
}

public class PrnIssuanceDto
{
    public string? TonnageBand { get; init; }
    public List<SignatoryDto> Signatories { get; init; } = [];
    public List<IncomeBusinessPlanItemDto> IncomeBusinessPlan { get; init; } = [];
}

public class SignatoryDto
{
    public string? FullName { get; init; }
    public string? Email { get; init; }
}

public class IncomeBusinessPlanItemDto
{
    public string? Description { get; init; }
    public string? DetailedDescription { get; init; }
    public int? PercentSpent { get; init; }
    public int? PercentIncomeSpent { get; init; }
    public string? UsageDescription { get; init; }
    public string? DetailedExplanation { get; init; }
}
