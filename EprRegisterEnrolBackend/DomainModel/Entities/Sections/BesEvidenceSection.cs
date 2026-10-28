using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

/// <summary>The application's BES evidence section (legacy <see cref="AccreditationApplicationBesEvidence"/>), with its status stored as a string.</summary>
public class BesEvidenceSection
{
    [BsonRepresentation(BsonType.String)]
    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;

    /// <summary>When the section was submitted, snapshotted on each submit and resubmit.</summary>
    public List<BesEvidenceSnapshot> Versions { get; set; } = [];
}
