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
    /// RA-739: snapshots the charge at submit: the request's total and each site's own (every site is chargeable).
    /// </summary>
    public void SetCharge(ChargeView charge)
    {
        ChargePence = charge.TotalPence;
        foreach (var site in Sites)
            site.ChargePence = charge.ChargePerSitePence;
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
    /// RA-698: the regulator's query on this item, answered by the operator's resubmit. At most one is open at a time
    /// (the item is <see cref="ChangeItemStatus.Queried"/>); a later query replaces the note and keeps the earlier
    /// <see cref="ItemQuery.QuerySubmissions"/>.
    /// </summary>
    public ItemQuery? Query { get; set; }

    /// <summary>
    /// RA-739: this site's own charge, the per-site in-year ORS fee, snapshotted at submit beside the request's
    /// total <see cref="AccreditationChangeRequest.ChargePence"/> and cleared with it when the request is reopened.
    /// </summary>
    public int? ChargePence { get; set; }
}

public class ChangeRequestSiteAnswers
{
    public string? Country { get; set; }
    public string? Coordinates { get; set; }
    public List<string>? OperationCodes { get; set; }
    public bool? ConditionsOfExport { get; set; }

    // RA-698: the values an operator changes while answering a query. Unlike the four above these override the ORS's
    // own values, which are not locked.
    public string? SiteName { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? TownOrCity { get; set; }
    public string? StateOrRegion { get; set; }
    public string? Postcode { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? RejectedLoadsArrangements { get; set; }

    /// <summary>RA-698: the queried ORS's interim sites the operator changed, keyed by the interim site's SiteId.</summary>
    public List<ChangeRequestInterimSiteAnswers> InterimSites { get; set; } = [];
}

/// <summary>RA-698: what the operator changed on one interim site of the queried ORS. Null means unchanged.</summary>
public class ChangeRequestInterimSiteAnswers
{
    public required int SiteId { get; set; }
    public string? SiteName { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? TownOrCity { get; set; }
    public string? StateOrRegion { get; set; }
    public string? Postcode { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
}

/// <summary>RA-698: a regulator's query on an item.</summary>
public class ItemQuery
{
    public required string QueryNote { get; set; }
    public DateTime RaisedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The BES files the item held when the query was raised, which the operator cannot remove: only files they add
    /// in response can be.
    /// </summary>
    public List<string> BesFileIdsAtQuery { get; set; } = [];

    /// <summary>The item's answers when the query was raised, so a response records only what it changed.</summary>
    public ChangeRequestSiteAnswers AnswersAtQuery { get; set; } = new();

    /// <summary>One per resubmit, oldest first.</summary>
    public List<QuerySubmission> QuerySubmissions { get; set; } = [];
}

/// <summary>RA-698: one response to a query: when, by whom, and what changed.</summary>
public class QuerySubmission
{
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public required string FullName { get; set; }
    public required string JobTitle { get; set; }
    public List<string> ChangedFields { get; set; } = [];
    public List<string> NewBesFileIds { get; set; } = [];
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
}

/// <summary>
/// RA-698: PATCH body for a queried item's editable fields, and for an interim site its own subset. A null property
/// leaves the stored answer unchanged. The locked fields are here only so the validator can refuse them with a 422
/// rather than silently ignore them.
/// </summary>
public class PatchQueriedSiteRequest
{
    public string? SiteName { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? TownOrCity { get; set; }
    public string? StateOrRegion { get; set; }
    public string? Postcode { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? RejectedLoadsArrangements { get; set; }

    // Locked: any value is refused.
    public string? Country { get; set; }
    public string? Coordinates { get; set; }
    public List<string>? OperationCodes { get; set; }
    public List<string>? BaselCodes { get; set; }
    public bool? ConditionsOfExport { get; set; }
}

/// <summary>RA-698: Case Management's query on an item, pushed by ManagementBe.</summary>
public class QueryFromCaseManagementRequest
{
    public required string Note { get; set; }
    public DateTime? RaisedAt { get; set; }
}

/// <summary>RA-698: the declaration that resubmits a queried item.</summary>
public class ResubmitItemRequest
{
    public required string FullName { get; set; }
    public required string JobTitle { get; set; }
    public string? Email { get; set; }
}

/// <summary>PATCH body: a null property leaves the stored answer unchanged.</summary>
public class PatchChangeRequestSiteRequest
{
    public string? Country { get; set; }
    public string? Coordinates { get; set; }
    public List<string>? OperationCodes { get; set; }
    public bool? ConditionsOfExport { get; set; }
}
