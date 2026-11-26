using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-492: an in-year change request against a granted (Approved) accreditation application -
/// today only "add one or more existing registered overseas reprocessing sites". It references
/// the sites by their existing <see cref="OverseasSiteModel.SiteId"/> and ORS id and never
/// writes to the approved application, so the association stays pending until a regulator
/// decision exists.
/// RA-697: the request holds items, each decided on its own: one per site in <see cref="Sites"/>, or a
/// single <see cref="TonnageBandUpgrade"/> with no sites. The request's own <see cref="Status"/> is
/// only its draft/submitted lifecycle; screens read the items' statuses.
/// </summary>
public class AccreditationChangeRequest
{
    /// <summary>The request's own type, sent to Case Management; also the type of an item adding an ORS.</summary>
    public const string AddOverseasSiteChangeType = "add-overseas-site";
    public const string UpdateOverseasSiteChangeType = "update-overseas-site";
    public const string UpgradeTonnageBandChangeType = "upgrade-tonnage-band";

    [BsonId]
    public string RequestId { get; set; } = Guid.NewGuid().ToString();

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
    /// RA-739: set when a submit was sent to Case Management but the request was reopened because no confirmed reply
    /// came back. Case Management may already hold the submission and replays a resubmit with the sites it first
    /// received, so from then on the request's sites can't be added or removed.
    /// </summary>
    public bool SentToCaseManagement { get; set; }

    /// <summary>Optimistic-concurrency token, bumped by every update.</summary>
    public long Version { get; set; }

    [BsonIgnore]
    [JsonIgnore]
    public IEnumerable<ChangeRequestItem> Items =>
        TonnageBandUpgrade is null ? Sites : [.. Sites, TonnageBandUpgrade];

    /// <summary>
    /// RA-739: snapshots the charge at submit: the request's total and each site's own. RA-682 AC05: only an add
    /// is chargeable; an update's own charge is 0.
    /// </summary>
    public void SetCharge(ChargeView charge)
    {
        ChargePence = charge.TotalPence;
        foreach (var site in Sites)
            site.ChargePence = site.IsUpdate ? 0 : charge.ChargePerSitePence;
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
public sealed record OverseasSiteDetails
{
    public string? SiteName { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? TownOrCity { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? RepatriatedLoads { get; set; }
}

/// <summary>RA-682 AC04: one interim site change within an ORS update.</summary>
public class InterimSiteChange
{
    public const string Change = "change";
    public const string Add = "add";
    public const string Remove = "remove";

    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid ChangeId { get; set; } = Guid.NewGuid();

    /// <summary><see cref="Change"/>, <see cref="Add"/> or <see cref="Remove"/>.</summary>
    public required string Action { get; set; }

    /// <summary>The existing interim site changed or removed. Null for an add, which has no id until it is granted.</summary>
    public int? InterimSiteId { get; set; }

    /// <summary>The interim site as it was, for a change or remove.</summary>
    public InterimSiteDetails? Previous { get; set; }

    /// <summary>The interim site as proposed, for a change or add.</summary>
    public InterimSiteDetails? Proposed { get; set; }
}

/// <summary>RA-682: an interim site's details, as <see cref="InterimSiteModel"/> holds them, without its ids.</summary>
public sealed record InterimSiteDetails
{
    public required string Country { get; set; }
    public required string SiteName { get; set; }
    public required string AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public required string TownOrCity { get; set; }
    public string? StateOrRegion { get; set; }
    public string? Postcode { get; set; }
    public required string ContactName { get; set; }
    public required string ContactEmail { get; set; }
    public required string ContactPhone { get; set; }
    public List<string> OperationCodes { get; set; } = [];

    // A record compares lists by reference; two interim sites' details are the same when their codes are.
    public bool Equals(InterimSiteDetails? other) =>
        other is not null
        && (Country, SiteName, AddressLine1, AddressLine2, TownOrCity, StateOrRegion, Postcode)
            == (other.Country, other.SiteName, other.AddressLine1, other.AddressLine2, other.TownOrCity, other.StateOrRegion, other.Postcode)
        && (ContactName, ContactEmail, ContactPhone) == (other.ContactName, other.ContactEmail, other.ContactPhone)
        && OperationCodes.SequenceEqual(other.OperationCodes);

    public override int GetHashCode() => HashCode.Combine(SiteName, AddressLine1, TownOrCity, ContactEmail);

    public static InterimSiteDetails From(InterimSiteModel interim) =>
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

    public static InterimSiteDetails From(AddInterimSiteRequest request) =>
        new()
        {
            Country = request.Country,
            SiteName = request.SiteName,
            AddressLine1 = request.AddressLine1,
            AddressLine2 = request.AddressLine2,
            TownOrCity = request.TownOrCity,
            StateOrRegion = request.StateOrRegion,
            Postcode = request.Postcode,
            ContactName = request.ContactName,
            ContactEmail = request.ContactEmail,
            ContactPhone = request.ContactPhone,
            OperationCodes = [.. request.OperationCodes],
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

/// <summary>PATCH body: a null property leaves the stored answer unchanged.</summary>
public class PatchChangeRequestSiteRequest
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

    internal bool HasDetails =>
        SiteName is not null
        || AddressLine1 is not null
        || AddressLine2 is not null
        || TownOrCity is not null
        || ContactName is not null
        || ContactEmail is not null
        || ContactPhone is not null
        || RepatriatedLoads is not null;
}
