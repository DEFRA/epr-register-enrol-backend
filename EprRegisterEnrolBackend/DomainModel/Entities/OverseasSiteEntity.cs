using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// One version of an Overseas Reprocessing Site (ORS), in <c>overseasSiteVersions</c>. Its interim sites are
/// embedded, so every interim site change is a new version of the ORS (D1). <see cref="OrsId"/> is the stable,
/// user-visible identity: three digits, allocated once per registration and never reissued.
/// </summary>
public class OverseasSiteEntity : VersionedEntityBase
{
    /// <summary>The Registration's <c>entityId</c>.</summary>
    public ObjectId RegistrationId { get; set; }

    /// <summary>Null only for legacy sites that never had one; unique per registration among current versions when set.</summary>
    public string? OrsId { get; set; }

    public string? SiteName { get; set; }
    public PostalAddress? Address { get; set; }
    public string? FormattedAddress { get; set; }
    public string? Country { get; set; }
    public bool IsEu { get; set; }
    public bool IsOecd { get; set; }
    public string? Coordinates { get; set; }
    public ContactPerson? Contact { get; set; }
    public List<string> OperationCodes { get; set; } = [];

    /// <summary>Basel/OECD waste codes as an ordered list. A maximum of three is a validation rule, not the storage shape.</summary>
    public List<string> BaselOecdCodes { get; set; } = [];

    public string? RepatriatedLoads { get; set; }
    public bool? ConditionsOfExport { get; set; }

    [BsonRepresentation(BsonType.String)]
    public OverseasSiteStatus Status { get; set; } = OverseasSiteStatus.Proposed;

    [BsonRepresentation(BsonType.String)]
    public SiteOrigin Origin { get; set; } = SiteOrigin.Operator;

    public ObjectId? ProposedByApplicationId { get; set; }

    public List<InterimSiteEntry> InterimSites { get; set; } = [];

    /// <summary>
    /// Site numbers that appear more than once in this ORS's own <see cref="InterimSites"/>. The unique index
    /// across the registration's ORSs does not catch a repeat inside one document, so the write path must
    /// check this is empty before it writes a version.
    /// </summary>
    public IReadOnlyList<string> DuplicateInterimSiteNumbers() =>
        InterimSites
            .GroupBy(s => s.SiteNumber)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
}
