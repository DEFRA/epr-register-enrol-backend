using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-492: an in-year change request against a granted (Approved) accreditation application: adding
/// existing registered overseas reprocessing sites and (RA-682) changing accredited ones' details. It references
/// the sites by their existing <see cref="OverseasSiteModel.SiteId"/> and ORS id and never
/// writes to the approved application, so the association stays pending until a regulator
/// decision exists.
/// RA-697: the request holds items, each decided on its own: one per site in <see cref="Sites"/>, or a
/// single <see cref="TonnageBandUpgrade"/> with no sites. The request's own <see cref="Status"/> is
/// only its draft/submitted lifecycle; screens read the items' statuses.
/// </summary>
public class AccreditationChangeRequest
{
    /// <summary>The type of an item adding a registered ORS (RA-492).</summary>
    public const string AddOverseasSiteChangeType = "add-overseas-site";
    public const string UpdateOverseasSiteChangeType = "update-overseas-site";
    public const string UpgradeTonnageBandChangeType = "upgrade-tonnage-band";

    [BsonId]
    public string RequestId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// RA-682: what the request changes as a whole: an add-ORS request when it adds any ORS (it is charged), an update
    /// when it only changes ORS details. Set from the items at submit; until then the view derives it
    /// (<see cref="ChangeRequestRules.RequestChangeType"/>), so a draft's stored value may be the default. Each item
    /// carries its own type, which is what Case Management is sent.
    /// </summary>
    public string ChangeType { get; set; } = AddOverseasSiteChangeType;

    public required string OrganisationId { get; set; }

    /// <summary>
    /// The Approved accreditation application this request changes. RA-697: null for a registration-only request,
    /// which is read by organisation, registration, material and year instead.
    /// </summary>
    public string? ApplicationId { get; set; }

    /// <summary>The ReEx registration id; what a registration-only request is keyed by.</summary>
    public string? RegistrationId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public required MaterialType MaterialType { get; set; }

    public required int Year { get; set; }

    public string? AccreditationReference { get; set; }

    [BsonRepresentation(BsonType.String)]
    public ChangeRequestStatus Status { get; set; } = ChangeRequestStatus.InProgress;

    public List<ChangeRequestSite> Sites { get; set; } = [];

    /// <summary>A tonnage band upgrade request's single item; such a request has no sites.</summary>
    public TonnageBandUpgradeItem? TonnageBandUpgrade { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? SubmittedAt { get; set; }

    public SubmittedByModel? SubmittedBy { get; set; }

    public string? PaymentReference { get; set; }

    /// <summary>The charge the operator confirmed, snapshotted at submit.</summary>
    public int? ChargePence { get; set; }

    /// <summary>
    /// RA-739: Case Management's <c>SUB-{yyyy}-{seq}</c> reference, shared by the work items one submission
    /// created. Stored for support and not shown to the operator: each item holds its own application reference.
    /// </summary>
    public string? SubmissionReference { get; set; }

    /// <summary>
    /// RA-682: the request's submission references, each once. Since RA-767 a request is sent to Case Management as
    /// one submission, so this holds one; it stays a list for requests stored before then. <see cref="SubmissionReference"/> is the first of them. Both
    /// are stored (a request submitted before RA-682 has only the one), so they are set together by
    /// <see cref="SetSubmissionReferences"/>.
    /// </summary>
    public List<string> SubmissionReferences { get; set; } = [];

    /// <summary>Sets the submission references and, from them, <see cref="SubmissionReference"/>: the first, or null.</summary>
    public void SetSubmissionReferences(IReadOnlyList<string> references)
    {
        SubmissionReferences = [.. references.Distinct()];
        SubmissionReference = references.Count > 0 ? references[0] : null;
    }

    /// <summary>
    /// RA-739: set when a submit was sent to Case Management but the request was reopened because no confirmed reply
    /// came back. Case Management may already hold the submission and replays a resubmit with the sites it first
    /// received, so from then on the request's sites can't be added or removed.
    /// </summary>
    public bool SentToCaseManagement { get; set; }

    /// <summary>
    /// RA-682: whether an item can still be changed. Not once Case Management may hold it: its replay would ignore the
    /// change. An item's own <see cref="ChangeRequestItem.CaseManagementMayHold"/> decides; one written before RA-682
    /// has none, and the request's <see cref="SentToCaseManagement"/> decides for it.
    /// </summary>
    public bool IsFixed(ChangeRequestItem item) => item.CaseManagementMayHold ?? SentToCaseManagement;

    /// <summary>Optimistic-concurrency token, bumped by every update.</summary>
    public long Version { get; set; }

    [BsonIgnore]
    [JsonIgnore]
    public IEnumerable<ChangeRequestItem> Items =>
        TonnageBandUpgrade is null ? Sites : [.. Sites, TonnageBandUpgrade];

    /// <summary>
    /// RA-739: snapshots the charge at submit: the request's total and each site's own. RA-682 AC05: an update's own
    /// charge is 0.
    /// </summary>
    public void SetCharge(ChargeView charge)
    {
        ChargePence = charge.TotalPence;
        foreach (var site in Sites)
            site.ChargePence = ChangeRequestRules.IsChargeable(site) ? charge.ChargePerSitePence : 0;
    }

    /// <summary>Clears the total and every site's charge together, when a request is reopened.</summary>
    public void ClearCharge()
    {
        ChargePence = null;
        foreach (var site in Sites)
            site.ChargePence = null;
    }

    /// <summary>Moves every item with the request: to Submitted on submit, back to InProgress on reopen.</summary>
    public void SetItemStatuses(ChangeItemStatus status)
    {
        foreach (var item in Items)
            item.Status = status;
    }
}

/// <summary>RA-697: one change within a request, with its own identity, type and status.</summary>
public abstract class ChangeRequestItem
{
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid ItemId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// One of the change-type constants on <see cref="AccreditationChangeRequest"/>, fixed once the item is made. A
    /// string, not an enum, because the stored and API values are those constants' kebab-case strings.
    /// </summary>
    public string ChangeType { get; init; }

    protected ChangeRequestItem(string changeType) => ChangeType = changeType;

    [BsonRepresentation(BsonType.String)]
    public ChangeItemStatus Status { get; set; } = ChangeItemStatus.InProgress;

    /// <summary>When a granted change took effect.</summary>
    public DateTime? ValidFrom { get; set; }

    public DateTime? WithdrawnAt { get; set; }

    /// <summary>
    /// RA-739: the application reference of this item's own Case Management work item, such as an add-ORS
    /// item's <c>IYC-ORS-{yyyy}-{seq}</c>. Null until submitted, and while submission to Case Management is off.
    /// </summary>
    public string? CaseManagementReference { get; set; }

    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid? CaseManagementWorkItemId { get; set; }

    /// <summary>
    /// RA-495: the <c>occurredAt</c> of the last Case Management status push applied to this item, its ordering
    /// watermark: a push at or before it is a duplicate or out of order and changes nothing.
    /// </summary>
    public DateTime? CaseManagementStatusUpdatedAt { get; set; }

    /// <summary>
    /// RA-682: set when a submit failed, to whether Case Management may hold this item's submission.
    /// </summary>
    public bool? CaseManagementMayHold { get; set; }
}

public class TonnageBandUpgradeItem : ChangeRequestItem
{
    public TonnageBandUpgradeItem()
        : base(AccreditationChangeRequest.UpgradeTonnageBandChangeType) { }

    [BsonRepresentation(BsonType.String)]
    public required PlannedTonnageBand RequestedBand { get; set; }
}

public class ChangeRequestSite : ChangeRequestItem
{
    public ChangeRequestSite()
        : base(AccreditationChangeRequest.AddOverseasSiteChangeType) { }

    public required int SiteId { get; set; }
    public string? OrsId { get; set; }
    public required string SiteName { get; set; }

    /// <summary>Only the values the operator supplied because the ORS had none.</summary>
    public ChangeRequestSiteAnswers Answers { get; set; } = new();

    public BesEvidenceModel? BesEvidence { get; set; }

    /// <summary>
    /// RA-739: this site's own charge, the per-site in-year ORS fee, snapshotted at submit beside the request's
    /// total <see cref="AccreditationChangeRequest.ChargePence"/> and cleared with it when the request is reopened.
    /// </summary>
    public int? ChargePence { get; set; }

    /// <summary>RA-682: an update-overseas-site item changes an accredited ORS's details rather than adding it.</summary>
    [BsonIgnore]
    [JsonIgnore]
    public bool IsUpdate => ChangeType == AccreditationChangeRequest.UpdateOverseasSiteChangeType;

    /// <summary>
    /// RA-682: an update's snapshot of the ORS's editable details when the change was started, so the operator,
    /// the regulator and AC07's history can see what changed. Null on an add item.
    /// </summary>
    public OverseasSiteDetails? Previous { get; set; }

    /// <summary>RA-682: an update's proposed details, starting as <see cref="Previous"/>. Null on an add item.</summary>
    public OverseasSiteDetails? Proposed { get; set; }

    /// <summary>RA-682 AC04: an update's interim site changes: changed, added and removed interim sites.</summary>
    public List<InterimSiteChange> InterimSiteChanges { get; set; } = [];
}

/// <summary>
/// RA-682: the ORS details an operator may change in year. Country, coordinates, recycling operations, Basel
/// codes, conditions of export and BES are locked (AC02), so they are not here. The ORS holds no state or
/// postcode, so neither is here either.
/// </summary>
public sealed record OverseasSiteDetails : IOverseasSiteDetailsFields
{
    public string? SiteName { get; init; }
    public string? AddressLine1 { get; init; }
    public string? AddressLine2 { get; init; }
    public string? TownOrCity { get; init; }
    public string? ContactName { get; init; }
    public string? ContactEmail { get; init; }
    public string? ContactPhone { get; init; }
    public string? RepatriatedLoads { get; init; }

    /// <summary>The details as an ORS, a PATCH body or another set of details holds them.</summary>
    public static OverseasSiteDetails From(IOverseasSiteDetailsFields fields) =>
        new()
        {
            SiteName = fields.SiteName,
            AddressLine1 = fields.AddressLine1,
            AddressLine2 = fields.AddressLine2,
            TownOrCity = fields.TownOrCity,
            ContactName = fields.ContactName,
            ContactEmail = fields.ContactEmail,
            ContactPhone = fields.ContactPhone,
            RepatriatedLoads = fields.RepatriatedLoads,
        };

    /// <summary>
    /// Each detail with how to read and set it, so the rules that work detail by detail (applying a PATCH, rebasing on
    /// the ORS) don't each list them. An optional detail (address line 2, phone) is cleared with a blank value.
    /// </summary>
    internal static IReadOnlyList<DetailField> Fields { get; } =
    [
        new(d => d.SiteName, (d, v) => d with { SiteName = v }),
        new(d => d.AddressLine1, (d, v) => d with { AddressLine1 = v }),
        new(d => d.AddressLine2, (d, v) => d with { AddressLine2 = v }, Optional: true),
        new(d => d.TownOrCity, (d, v) => d with { TownOrCity = v }),
        new(d => d.ContactName, (d, v) => d with { ContactName = v }),
        new(d => d.ContactEmail, (d, v) => d with { ContactEmail = v }),
        new(d => d.ContactPhone, (d, v) => d with { ContactPhone = v }, Optional: true),
        new(d => d.RepatriatedLoads, (d, v) => d with { RepatriatedLoads = v }),
    ];

    internal sealed record DetailField(
        Func<OverseasSiteDetails, string?> Get,
        Func<OverseasSiteDetails, string?, OverseasSiteDetails> Set,
        bool Optional = false
    )
    {
        /// <summary>A sent value as it is kept: trimmed, and a blank optional one cleared.</summary>
        public string? Clean(string value) => Optional && string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>
/// RA-682 AC04: what an interim site change does. Stored and sent as <c>change</c>, <c>add</c> or <c>remove</c>,
/// the strings it was before it was an enum.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<InterimSiteChangeAction>))]
public enum InterimSiteChangeAction
{
    [JsonStringEnumMemberName("change")]
    Change,

    [JsonStringEnumMemberName("add")]
    Add,

    [JsonStringEnumMemberName("remove")]
    Remove,
}

/// <summary>RA-682 AC04: one interim site change within an ORS update.</summary>
public class InterimSiteChange
{
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid ChangeId { get; set; } = Guid.NewGuid();

    [BsonSerializer(typeof(JsonNamedEnumSerializer<InterimSiteChangeAction>))]
    public required InterimSiteChangeAction Action { get; set; }

    /// <summary>
    /// The existing interim site changed or removed. Null for an add until it is granted; RA-782: the grant then records
    /// the id it allocated, so the grant run again finds the interim site rather than adding it twice.
    /// </summary>
    public int? InterimSiteId { get; set; }

    /// <summary>The interim site as it was, for a change or remove.</summary>
    public InterimSiteDetails? Previous { get; set; }

    /// <summary>The interim site as proposed, for a change or add.</summary>
    public InterimSiteDetails? Proposed { get; set; }
}

/// <summary>RA-682: an interim site's details, as <see cref="InterimSiteModel"/> holds them, without its ids.</summary>
public sealed record InterimSiteDetails : IInterimSiteFields
{
    public required string Country { get; init; }
    public required string SiteName { get; init; }
    public required string AddressLine1 { get; init; }
    public string? AddressLine2 { get; init; }
    public required string TownOrCity { get; init; }
    public string? StateOrRegion { get; init; }
    public string? Postcode { get; init; }
    public required string ContactName { get; init; }
    public required string ContactEmail { get; init; }
    public required string ContactPhone { get; init; }
    public List<string> OperationCodes { get; init; } = [];

    // A record compares lists by reference; two interim sites' details are the same when their codes are.
    public bool Equals(InterimSiteDetails? other) =>
        other is not null
        && (Country, SiteName, AddressLine1, AddressLine2, TownOrCity, StateOrRegion, Postcode)
            == (other.Country, other.SiteName, other.AddressLine1, other.AddressLine2, other.TownOrCity, other.StateOrRegion, other.Postcode)
        && (ContactName, ContactEmail, ContactPhone) == (other.ContactName, other.ContactEmail, other.ContactPhone)
        && OperationCodes.SequenceEqual(other.OperationCodes);

    public override int GetHashCode() => HashCode.Combine(SiteName, AddressLine1, TownOrCity, ContactEmail);

    /// <summary>A stored interim site's details, or a request's for one.</summary>
    public static InterimSiteDetails From(IInterimSiteFields interim) =>
        new()
        {
            Country = interim.Country,
            SiteName = interim.SiteName,
            AddressLine1 = interim.AddressLine1,
            AddressLine2 = interim.AddressLine2,
            TownOrCity = interim.TownOrCity,
            StateOrRegion = interim.StateOrRegion,
            Postcode = interim.Postcode,
            ContactName = interim.ContactName,
            ContactEmail = interim.ContactEmail,
            ContactPhone = interim.ContactPhone,
            OperationCodes = [.. interim.OperationCodes],
        };
}

public class ChangeRequestSiteAnswers
{
    public string? Country { get; set; }
    public string? Coordinates { get; set; }
    public List<string>? OperationCodes { get; set; }
    public bool? ConditionsOfExport { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeRequestStatus
{
    InProgress,
    Submitted,
}

/// <summary>
/// RA-697: an item's status, named as the application statuses are so their labels and tags apply.
/// Only the dev seed writes anything beyond Submitted until Case Management decides items.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeItemStatus
{
    InProgress,
    Submitted,
    Queried,
    Updated,
    DulyMade,
    Approved,
    Rejected,
    Withdrawn,
}

public static class ChangeItemStatusExtensions
{
    /// <summary>In flight: not yet granted (Approved), refused (Rejected) or withdrawn.</summary>
    public static bool IsActive(this ChangeItemStatus status) =>
        status
            is ChangeItemStatus.InProgress
                or ChangeItemStatus.Submitted
                or ChangeItemStatus.Queried
                or ChangeItemStatus.Updated
                or ChangeItemStatus.DulyMade;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SiteAccreditationStatus
{
    Accredited,
    Registered,
}

public class AddChangeRequestSiteRequest
{
    public required int SiteId { get; set; }

    /// <summary>
    /// RA-682: <c>add-overseas-site</c> (the default, RA-492's) to add a registered ORS, or
    /// <c>update-overseas-site</c> to change an accredited ORS's details.
    /// </summary>
    public string? ChangeType { get; set; }
}

/// <summary>
/// PATCH body: a null property leaves the stored answer unchanged. RA-682: an update item's details are flat fields
/// here, as the frontend sends them; <see cref="OverseasSiteDetails.From"/> reads them.
/// </summary>
public class PatchChangeRequestSiteRequest : IOverseasSiteDetailsFields
{
    public string? Country { get; set; }
    public string? Coordinates { get; set; }
    public List<string>? OperationCodes { get; set; }
    public bool? ConditionsOfExport { get; set; }

    // RA-682: an update item's editable details. Absent means unchanged.
    public string? SiteName { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? TownOrCity { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? RepatriatedLoads { get; set; }

    internal bool HasDetails => OverseasSiteDetails.From(this) != new OverseasSiteDetails();
}
