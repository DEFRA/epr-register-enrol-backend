using EprRegisterEnrolBackend.DomainModel.Entities.Sections;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// The details of a New or Reapply accreditation application. Every field here is valid only for those two kinds,
/// which is why none of them is on the envelope (register-enrol-application-types.md, "Can the accreditation-shaped
/// fields stay on a shared shape?").
/// </summary>
[BsonDiscriminator(nameof(ApplicationType.Accreditation))]
public sealed class AccreditationDetails : ApplicationDetails
{
    public override ApplicationType ApplicationType => ApplicationType.Accreditation;

    [BsonRepresentation(BsonType.String)]
    public required AccreditationKind Kind { get; set; }

    /// <summary>The accreditation year applied for.</summary>
    public int Year { get; set; }

    /// <summary>The Accreditation (prior year) this application was seeded from. Null for New and for migrated 2027 applications (D10).</summary>
    public ObjectId? SourceAccreditationId { get; set; }

    public PrnsSection Prns { get; set; } = new();
    public BusinessPlanSection BusinessPlan { get; set; } = new();
    public SamplingPlanSection SamplingPlan { get; set; } = new();
    public BesEvidenceSection? BesEvidence { get; set; }

    /// <summary>The overseas sites section's status and submission history. The sites are <see cref="ApplicationOverseasSites"/>.</summary>
    public OverseasSitesSection? OverseasSitesSection { get; set; }

    /// <summary>One entry per overseas site this application touches.</summary>
    public List<ApplicationOverseasSiteEntry> ApplicationOverseasSites { get; set; } = [];

    public string? ReservedAccreditationNumber { get; set; }

    /// <summary>Numbers this application held before <see cref="ReservedAccreditationNumber"/>.</summary>
    public List<string> PreviousReservedAccreditationNumbers { get; set; } = [];

    /// <summary>
    /// RA-763: the operator's accreditation number for last year, with its year, the basis for this year's. Null when
    /// they were registered only last year. Unlike <see cref="PreviousReservedAccreditationNumbers"/>, it is not a
    /// number this application held.
    /// </summary>
    public AccreditationNumberForYear? LastYearsAccreditation { get; set; }

    /// <summary>ReEx's id for the prior-year accreditation this was seeded from. Copied to the Accreditation at approval.</summary>
    public string? SourceReExAccreditationId { get; set; }

    /// <summary>The year of the ReEx data this application was seeded from.</summary>
    public int? SeedDataSourceYear { get; set; }

    /// <summary>Set when the application is approved: the Accreditation it created.</summary>
    public ObjectId? AccreditationId { get; set; }

    public override IEnumerable<ApplicationOverseasSiteEntry> HeldSites() =>
        ApplicationOverseasSites;

    /// <summary>One accreditation application per registration and year, whether New or Reapply.</summary>
    public override string? ExclusivityKey(ApplicationEntity application) =>
        application.RegistrationId is { } registrationId && Year > 0
            ? $"accreditation|{registrationId}|{Year}"
            : null;

    /// <summary>
    /// An approved accreditation application keeps its key: a cancelled Accreditation is reinstated as a new version
    /// of itself, never replaced by a new application for the same registration and year (Q36).
    /// </summary>
    public override bool ReleasesExclusivityKeyOnApproval() => false;
}
