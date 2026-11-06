using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// The details of an in-year increase to an Accreditation's tonnage band. It touches no overseas site, so it holds
/// none. Increase-only and one-open-per-Accreditation are rules for application code, not this shape.
/// </summary>
[BsonDiscriminator(nameof(ApplicationType.TonnageChange))]
public sealed class TonnageChangeDetails : ApplicationDetails
{
    public override ApplicationType ApplicationType => ApplicationType.TonnageChange;

    /// <summary>The Accreditation (its <c>entityId</c>) whose band changes.</summary>
    public ObjectId TargetAccreditationId { get; set; }

    /// <summary>The band when the change was made, stored as the ReEx value like the Accreditation's.</summary>
    [BsonSerializer(typeof(TonnageBandSerializer))]
    public TonnageBand? FromBand { get; set; }

    [BsonSerializer(typeof(TonnageBandSerializer))]
    public TonnageBand? ToBand { get; set; }

    /// <summary>One open change per Accreditation.</summary>
    public override string? ExclusivityKey(ApplicationEntity application) =>
        TargetAccreditationId != ObjectId.Empty ? $"tonnage-change|{TargetAccreditationId}" : null;

    /// <summary>The next change to the same Accreditation is a new application.</summary>
    public override bool ReleasesExclusivityKeyOnApproval() => true;
}
