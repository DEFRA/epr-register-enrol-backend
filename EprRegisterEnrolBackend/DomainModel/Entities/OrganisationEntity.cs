using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// A synced copy of a ReEx organisation, held in <c>organisations</c>. Not versioned (D7): a sync overwrites it
/// in place. It may later shrink to an Account Management id plus data this service augments it with. Values
/// ReEx owns, such as business and partnership types, are kept as ReEx sends them.
/// </summary>
public class OrganisationEntity
{
    [BsonId]
    public ObjectId Id { get; set; }

    public required string ReExOrganisationId { get; set; }

    /// <summary>ReEx's numeric organisation number, the value a person actually sees.</summary>
    public int? OrganisationNumber { get; set; }

    public string? Name { get; set; }
    public string? TradingName { get; set; }
    public string? CompanyRegistrationNumber { get; set; }
    public string? CompaniesHouseNumber { get; set; }
    public PostalAddress? RegisteredAddress { get; set; }
    public PostalAddress? Address { get; set; }
    public string? BusinessType { get; set; }
    public Partnership? Partnership { get; set; }
    public ContactPerson? ContactDetails { get; set; }
    public ContactPerson? ManagementContactDetails { get; set; }
    public ContactPerson? SubmitterContactDetails { get; set; }
    public List<OrganisationUser> Users { get; set; } = [];
    public List<string> WasteProcessingTypes { get; set; } = [];
    public List<string> ReprocessingNations { get; set; } = [];
    public string? SubmittedToRegulator { get; set; }
    public string? LinkedDefraOrganisationId { get; set; }
    public EntitySource Source { get; set; } = new();
    public DateTime UpdatedAt { get; set; }
}
