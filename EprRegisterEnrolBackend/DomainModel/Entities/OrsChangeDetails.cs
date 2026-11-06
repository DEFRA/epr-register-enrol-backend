using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// The details of an in-year change to one overseas reprocessing site of a 2027+ Accreditation. A batch of sites is
/// one application per ORS, linked by the envelope's <see cref="ApplicationEntity.SubmissionReference"/>
/// (register-enrol-application-types.md).
/// </summary>
[BsonDiscriminator(nameof(ApplicationType.OrsChange))]
public sealed class OrsChangeDetails : ApplicationDetails
{
    public override ApplicationType ApplicationType => ApplicationType.OrsChange;

    /// <summary>The Accreditation (its <c>entityId</c>) this change applies to.</summary>
    public ObjectId TargetAccreditationId { get; set; }

    /// <summary>The ORS (its <c>entityId</c>) this change is about. A new ORS has one from the moment it is proposed.</summary>
    public ObjectId OverseasSiteId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public List<OrsChangeKind> ChangeKinds { get; set; } = [];

    /// <summary>Set when the operator makes the declaration, at submission.</summary>
    public OrsChangeDeclaration? Declaration { get; set; }

    /// <summary>
    /// The site as this change proposes it, with any <c>proposedChanges</c> overlay and interim sites, as on an
    /// accreditation application. Its <see cref="ApplicationOverseasSiteEntry.OverseasSiteId"/> is
    /// <see cref="OverseasSiteId"/>.
    /// </summary>
    public required ApplicationOverseasSiteEntry Site { get; set; }

    public override IEnumerable<ApplicationOverseasSiteEntry> HeldSites() => [Site];
}
