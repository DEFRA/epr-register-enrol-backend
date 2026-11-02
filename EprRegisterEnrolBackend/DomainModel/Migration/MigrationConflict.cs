using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>
/// Something the migration could not settle by rule alone, held in <c>migrationConflicts</c> for the reconciliation
/// report (design doc §6.1). Ids, ORS ids and site numbers only: never names, addresses or contact details.
/// </summary>
public class MigrationConflict
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonRepresentation(BsonType.String)]
    public required MigrationConflictKind Kind { get; set; }

    public required string ReExRegistrationId { get; set; }

    /// <summary>The Registration's <c>entityId</c>, when the migration got that far.</summary>
    public ObjectId? RegistrationId { get; set; }

    public string? OrsId { get; set; }
    public string? SiteNumber { get; set; }

    /// <summary>The application whose values were kept, when one was chosen.</summary>
    public ObjectId? WinnerApplicationId { get; set; }

    /// <summary>The applications concerned; for a details conflict, the ones whose values were not kept.</summary>
    public List<ObjectId> ApplicationIds { get; set; } = [];

    public DateTime RecordedAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationConflictKind
{
    /// <summary>Applications disagree on an ORS's details; the newest approved, else the newest, won.</summary>
    OrsDetailsDiffer,

    /// <summary>Applications disagree on an interim site's details.</summary>
    InterimSiteDetailsDiffer,

    /// <summary>A legacy site with no <c>orsId</c>: migrated as a Proposed ORS without one, never given an invented id (R3).</summary>
    OrsIdMissing,

    /// <summary>An interim site number not in the three-digit format, or already used in the registration, was renumbered.</summary>
    InterimSiteRenumbered,

    /// <summary>More than one live application for the same material and year: all linked, none merged (§6.3).</summary>
    DuplicateLiveApplications,

    /// <summary>Applications disagree on the registration number; the newest approved, else the newest, won.</summary>
    RegistrationNumberDiffers,

    /// <summary>An approved application with no accreditation number: its Accreditation was created without one.</summary>
    AccreditationNumberMissing,

    /// <summary>An approved application whose registration already has an Accreditation for that year.</summary>
    AccreditationAlreadyExists,
}
