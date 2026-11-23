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

    /// <summary>
    /// When this site was first approved for the customer, which may be many years before this version. Written once
    /// and never overwritten. Not the versioning <c>validFrom</c>, which is when this version was recorded.
    /// </summary>
    public DateTime? FirstApprovedAt { get; set; }

    /// <summary>
    /// Where <see cref="FirstApprovedAt"/> came from. Only ReEx's date is served as the sites' <c>validFrom</c> (see
    /// <see cref="LegacyValidFrom"/>).
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public FirstApprovalSource? FirstApprovalSource { get; set; }

    /// <summary>
    /// ReEx's <c>validFrom</c> for <see cref="FirstApprovedAt"/>, exactly as received ("2026-01-01",
    /// "2026-01-01T00:00:00.000Z" and other shapes), so the read model serves what legacy holds (RA-705, decided
    /// 2026-11-16). Set only with a ReEx date; null for a date approved here, and on sites recorded before it existed.
    /// </summary>
    public string? ReExValidFrom { get; set; }

    public List<InterimSiteEntry> InterimSites { get; set; } = [];

    /// <summary>Sets the first approved date unless the site already has one: it is written once and never overwritten.</summary>
    public void RecordFirstApproval(
        DateTime at,
        FirstApprovalSource source,
        string? reExValidFrom = null
    )
    {
        if (FirstApprovedAt is null)
        {
            FirstApprovedAt = at;
            FirstApprovalSource = source;
            ReExValidFrom = ReExStringFor(source, reExValidFrom);
        }
    }

    /// <summary>
    /// For a version being built from a legacy site, which sets the date that site holds: an earlier or better one
    /// replaces it before the version is saved. Does nothing when there is none.
    /// </summary>
    public void AdoptFirstApproval(
        DateTime? at,
        FirstApprovalSource? source,
        string? reExValidFrom = null
    )
    {
        if (at is not null)
        {
            FirstApprovedAt = at;
            FirstApprovalSource = source;
            ReExValidFrom = source is { } known ? ReExStringFor(known, reExValidFrom) : null;
        }
    }

    /// <summary>
    /// The site's <c>validFrom</c> as a legacy application holds it, for an application whose Accreditation records no
    /// date of its own for the site (<see cref="AccreditationOverseasSiteEntry.AccreditedFrom"/>): ReEx's string as
    /// received (an ORS recorded before the string was kept gives its date in the one form the model has). Null for a
    /// date from this service: legacy never writes an application's approval, and writes a granted change request's
    /// only onto the granted accreditation's site, which serves its entry's date (3bb.43, 3bb.44).
    /// </summary>
    public string? LegacyValidFrom() =>
        FirstApprovalSource == Entities.FirstApprovalSource.ReEx
            ? ReExValidFrom ?? SiteApprovalDate.Format(FirstApprovedAt)
            : null;

    private static string? ReExStringFor(FirstApprovalSource source, string? reExValidFrom) =>
        source == Entities.FirstApprovalSource.ReEx ? reExValidFrom : null;

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
