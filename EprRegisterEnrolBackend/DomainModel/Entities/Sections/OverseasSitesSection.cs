using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

/// <summary>
/// The status and submission history of the application's overseas sites section (legacy
/// <see cref="AccreditationApplicationOverseasSites"/>). The sites themselves are not here: they are the
/// application's <see cref="ApplicationEntity.ApplicationOverseasSites"/> and the OverseasSite entities.
/// </summary>
public class OverseasSitesSection
{
    [BsonRepresentation(BsonType.String)]
    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;

    /// <summary>What was submitted, snapshotted on each submit and resubmit, in the legacy site shape.</summary>
    public List<OverseasSitesSnapshot> Versions { get; set; } = [];
}
