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

    /// <summary>
    /// ReEx's numeric organisation number, the value a person actually sees: the public register's "Org Id"
    /// (e.g. 500027 in the Public register of reprocessors and exporters). The legacy application holds it as
    /// <c>orgId</c>.
    /// </summary>
    public int? OrganisationNumber { get; set; }

    /// <summary>
    /// Takes <paramref name="organisationNumber"/> only while the organisation has none. ReEx never changes an
    /// organisation's number, so one already held is kept. Every write path (seed, dual-write and migration) goes
    /// through this rule. Returns whether the number was set.
    /// </summary>
    public bool FillOrganisationNumber(int? organisationNumber)
    {
        if (OrganisationNumber is not null || organisationNumber is null)
        {
            return false;
        }

        OrganisationNumber = organisationNumber;
        return true;
    }

    public string? Name { get; set; }
    public string? TradingName { get; set; }
    public string? CompanyRegistrationNumber { get; set; }
    public string? CompaniesHouseNumber { get; set; }
    public PostalAddress? RegisteredAddress { get; set; }

    /// <summary>
    /// The registered address as one string. Legacy applications hold only this, and it cannot be reliably split back
    /// into <see cref="RegisteredAddress"/>, so the migration and the read model both need it.
    /// </summary>
    public string? FormattedRegisteredAddress { get; set; }

    /// <summary>True when the address came from ReEx's registered address, false when it fell back to its address.</summary>
    public bool? IsUkRegisteredAddress { get; set; }

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
