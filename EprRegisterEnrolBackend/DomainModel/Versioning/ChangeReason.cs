using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.DomainModel.Versioning;

/// <summary>
/// Why a new version of an entity was written (docs/analysis/register-enrol-domain-model-design.md, §4.2).
/// Stored as a string on <see cref="ChangeInfo.Reason"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeReason
{
    ReExImport,
    ReExRefresh,
    ApplicationApproved,
    Suspended,
    Unsuspended,
    Cancelled,
    Reinstated,
    RegistrationCancelled,
    InterimSiteAdded,
    InterimSiteUpdated,
    InterimSiteRemoved,
    InterimSiteReadded,
    OrsSuspended,
    OrsUnsuspended,
    TonnageBandChanged,
    NumberRegenerated,
    LegacySiteNumberRenumbered,
    Migration,

    /// <summary>An operator added a site in an application: the new ORS is Proposed (B8c, option B).</summary>
    SiteProposed,

    /// <summary>The application that proposed a site edited it before a decision: written through to the ORS.</summary>
    ProposedSiteEdited,

    /// <summary>The application that proposed a site removed it before a decision.</summary>
    ProposedSiteDiscarded,

    /// <summary>The application that proposed an interim site dropped it before a decision (B8d).</summary>
    ProposedInterimSiteDiscarded,

    /// <summary>A write path changed the legacy application; its Application version follows it.</summary>
    ApplicationEdited,
}
