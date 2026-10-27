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
}
