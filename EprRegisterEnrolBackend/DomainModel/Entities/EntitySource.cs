using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>Where an entity's data came from, and whether it still needs enriching from ReEx (Q35: deferred).</summary>
public class EntitySource
{
    [BsonRepresentation(BsonType.String)]
    public SourceSystem System { get; set; } = SourceSystem.Application;

    /// <summary>When the data was last copied in. A refresh that changes only this writes no new version (§4.2).</summary>
    [ExcludedFromChangeDetection]
    public DateTime? ImportedAt { get; set; }

    public string? ReExSchemaVersion { get; set; }
    public bool EnrichmentPending { get; set; }
}
