using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

// RA-469 AC15/AC19: a minimal, narrowly-scoped audit record for the regulator-facing
// recycling-operations PATCH endpoint only - not a generic audit framework. Entirely separate
// from AccreditationApplicationOverseasSites.Versions (the existing [JsonIgnore] snapshot list,
// which is submit-time-only, populated only by Submit/Resubmit) - this collection is never read
// by, and never written to by, that mechanism.
public class RecyclingOperationsAuditRecord
{
    [BsonId(IdGenerator = typeof(ObjectIdGenerator))]
    public ObjectId? Id { get; set; }

    // For PatchRecyclingOperations (CaseManagement scheme): CaseManagementAuthenticationHandler's
    // "cdp_user_id"/"cdp_user_name" claims (in turn from the x-cdp-user-id/x-cdp-user-name request
    // headers) - the real per-caseworker identity. For UpdateOverseasSite (Frontend scheme): no
    // per-operator identity is available - FrontendAuthenticationHandler is a service-to-service
    // shared secret only - so CdpUserId carries that scheme's own NameIdentifier claim
    // ("epr-register-enrol-frontend") and CdpUserName carries an explanatory string rather than a
    // real name, pending real operator-identity forwarding from the frontend (tracked follow-up,
    // see UpdateOverseasSite's own audit comment). Both may also be empty when the caller didn't
    // send them at all - e.g. the Development header-trust bypass when no shared secret is
    // configured - so this is deliberately a plain string, not a required/non-nullable field.
    // The endpoint must not fail to record an edit just because identity is unavailable.
    public string CdpUserId { get; set; } = string.Empty;
    public string CdpUserName { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public required string OrganisationId { get; set; }
    public required string ApplicationId { get; set; }
    public required int SiteId { get; set; }

    public List<string> BeforeCodes { get; set; } = [];
    public List<string> AfterCodes { get; set; } = [];
}
