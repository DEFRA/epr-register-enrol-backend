using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// One version of a Registration, in <c>registrationVersions</c>. Registrations follow ReEx's lifecycle until
/// this service implements new registrations. The further ReEx fields (permits, yearly metrics, approved
/// persons and so on) arrive with enrichment, which is deferred (Q35).
/// </summary>
public class RegistrationEntity : VersionedEntityBase
{
    /// <summary>The Organisation's <c>_id</c> (Organisation is not versioned).</summary>
    public ObjectId OrganisationId { get; set; }

    public string? ReExRegistrationId { get; set; }

    /// <summary>Minted here, not in ReEx. Unique among current versions.</summary>
    public string? RegistrationNumber { get; set; }

    public List<string> PreviousRegistrationNumbers { get; set; } = [];

    [BsonRepresentation(BsonType.String)]
    public RegistrationStatus Status { get; set; } = RegistrationStatus.Created;

    [BsonRepresentation(BsonType.String)]
    public WasteProcessingKind WasteProcessingType { get; set; }

    [BsonRepresentation(BsonType.String)]
    public MaterialType Material { get; set; }

    [BsonRepresentation(BsonType.String)]
    public GlassRecyclingProcess? GlassRecyclingProcess { get; set; }

    [BsonRepresentation(BsonType.String)]
    public Nation? Nation { get; set; }

    public RegistrationSite? Site { get; set; }
    public PostalAddress? NoticeAddress { get; set; }
    public ContactPerson? ContactDetails { get; set; }
    public ContactPerson? SubmitterContactDetails { get; set; }
    public DateTime? FormSubmissionTime { get; set; }
    public EntitySource Source { get; set; } = new();
}
