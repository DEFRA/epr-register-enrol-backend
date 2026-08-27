using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.Organisation.Models;

// Local/dev/test fixture model only - backs FakeOrganisationPersistence, which StubReExApiAdapter
// uses in place of a real ReEx call. Not on the production ReEx HTTP path (see OrganisationDto in
// ReEx/Dtos, which the real HttpReExApiAdapter uses) and not kept in exact sync with it: e.g. OrgId
// here is non-nullable int (vs OrganisationDto's int?) and there is no ObjectId-shaped Id field at
// all, so this model doesn't reproduce the Id-vs-OrgId distinction at the heart of RA-503.
public class OrganisationModel
{
    public int OrgId { get; set; }

    public int SchemaVersion { get; set; }

    public int Version { get; set; }

    public List<string>? WasteProcessingTypes { get; set; }

    public List<string>? ReprocessingNations { get; set; }

    public string? BusinessType { get; set; }

    public CompanyDetailsModel? CompanyDetails { get; set; }

    public PartnershipModel? Partnership { get; set; }

    public ContactDetailsModel? ContactDetails { get; set; }

    public ContactDetailsModel? SubmitterContactDetails { get; set; }

    public string? SubmittedToRegulator { get; set; }

    public List<PersonModel>? Users { get; set; }

    public List<RegistrationModel>? Registrations { get; set; }

    public List<AccreditationModel>? Accreditations { get; set; }

    public string? FormSubmissionRawDataId { get; set; }
}

public class OrganisationSummaryModel
{
    public int OrgId { get; set; }
    public List<string>? WasteProcessingTypes { get; set; }
    public List<string>? ReprocessingNations { get; set; }
    public string? BusinessType { get; set; }
    public CompanyDetailsModel? CompanyDetails { get; set; }
    public PartnershipModel? Partnership { get; set; }
    public ContactDetailsModel? ContactDetails { get; set; }
    public string? SubmittedToRegulator { get; set; }
}

public class CompanyDetailsModel
{
    public string? Name { get; set; }
    public string? TradingName { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? CompaniesHouseNumber { get; set; }
    public RegisteredAddressModel? RegisteredAddress { get; set; }
}

public class RegisteredAddressModel
{
    public required string Line1 { get; set; }
    public string? Line2 { get; set; }
    public required string Town { get; set; }
    public string? County { get; set; }
    public string? Country { get; set; }
    public required string Postcode { get; set; }
    public string? Region { get; set; }
}

public class PartnershipModel
{
    public string? Type { get; set; }
    public List<PartnerModel>? Partners { get; set; }
}

public class PartnerModel
{
    public required string Name { get; set; }
    public required string Type { get; set; }
}

public class ContactDetailsModel
{
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Role { get; set; }
    public string? Title { get; set; }
}

public class PersonModel
{
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Title { get; set; }
    public string? Role { get; set; }
}

[BsonNoId]
public class RegistrationModel
{
    [BsonElement("id")]
    public ObjectId Id { get; set; }
    public string? SiteId { get; set; }
    public string? FormSubmissionTime { get; set; }
    public required string Status { get; set; }
    public required string Material { get; set; }
    public string? GlassRecyclingProcess { get; set; }
    public required string WasteProcessingType { get; set; }
    public ObjectId? AccreditationId { get; set; }
    public string? GridReference { get; set; }
    public string? WasteRegistrationNumber { get; set; }
    public string? Suppliers { get; set; }
    public string? PlantEquipmentDetails { get; set; }
    public string? FormSubmissionRawDataId { get; set; }
    public SiteAddressModel? SiteAddress { get; set; }
    public NoticeAddressModel? NoticeAddress { get; set; }
    public List<string>? RecyclingProcess { get; set; }
    public List<string>? ExportPorts { get; set; }
    public List<WasteManagementPermitModel>? WasteManagementPermits { get; set; }
    public List<PersonModel>? ApprovedPersons { get; set; }
    public YearlyMetricsModel? YearlyMetrics { get; set; }
    public ContactDetailsModel? ContactDetails { get; set; }
    public ContactDetailsModel? SubmitterContactDetails { get; set; }
    public List<string>? SamplingInspectionPlan { get; set; }
    public List<string>? OverseasSites { get; set; }
}

public class SiteAddressModel
{
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? Town { get; set; }
    public string? County { get; set; }
    public string? Country { get; set; }
    public string? Postcode { get; set; }
}

public class NoticeAddressModel
{
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? Town { get; set; }
    public string? County { get; set; }
    public string? Country { get; set; }
    public string? Postcode { get; set; }
}

public class WasteManagementPermitModel
{
    public string? Type { get; set; }
    public string? PermitNumber { get; set; }
    public string? AuthorisedWeight { get; set; }
    public string? PermitWindow { get; set; }
    public List<ExemptionModel>? Exemptions { get; set; }
}

public class ExemptionModel
{
    public string? Reference { get; set; }
    public string? ExemptionCode { get; set; }
}

public class YearlyMetricsModel
{
    public string? Year { get; set; }
    public string? Metric { get; set; }
    public MetricsInputModel? Input { get; set; }
    public MetricsOutputModel? Output { get; set; }
}

public class MetricsInputModel
{
    public string? Type { get; set; }
    public int? UkPackagingWaste { get; set; }
    public int? NonUkPackagingWaste { get; set; }
    public int? NonPackagingWaste { get; set; }
}

public class MetricsOutputModel
{
    public string? Type { get; set; }
    public int? SentToAnotherSite { get; set; }
    public int? Contaminants { get; set; }
    public int? ProcessLoss { get; set; }
}

[BsonNoId]
public class AccreditationModel
{
    [BsonElement("id")]
    public ObjectId Id { get; set; }
    public string? FormSubmissionTime { get; set; }
    public required string Status { get; set; }
    public required string Material { get; set; }
    public string? GlassRecyclingProcess { get; set; }
    public required string WasteProcessingType { get; set; }
    public string? FormSubmissionRawDataId { get; set; }
    public SiteAddressModel? SiteAddress { get; set; }
    public List<string>? RecyclingProcess { get; set; }
    public PrnIssuanceModel? PrnIssuance { get; set; }
    public List<BusinessPlanItemModel>? BusinessPlan { get; set; }
    public ContactDetailsModel? ContactDetails { get; set; }
    public ContactDetailsModel? SubmitterContactDetails { get; set; }
    public List<string>? SamplingInspectionPlan { get; set; }
    public List<string>? OverseasSites { get; set; }
}

public class PrnIssuanceModel
{
    public string? PlannedIssuance { get; set; }
    public List<PersonModel>? Signatories { get; set; }
    public List<PrnIncomeBusinessPlanItemModel>? PrnIncomeBusinessPlan { get; set; }
}

public class PrnIncomeBusinessPlanItemModel
{
    public string? Description { get; set; }
    public string? DetailedDescription { get; set; }
    public int? PercentSpent { get; set; }
    public int? PercentIncomeSpent { get; set; }
    public string? UsageDescription { get; set; }
    public string? DetailedExplanation { get; set; }
}

public class BusinessPlanItemModel
{
    public string? Description { get; set; }
    public string? DetailedDescription { get; set; }
    public int? PercentSpent { get; set; }
}
