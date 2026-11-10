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

    public string? CaseManagementReference { get; set; }

    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid? CaseManagementWorkItemId { get; set; }

    /// <summary>Optimistic-concurrency token, bumped by every update.</summary>
    public long Version { get; set; }

    [BsonIgnore]
    [JsonIgnore]
    public IEnumerable<ChangeRequestItem> Items =>
        TonnageBandUpgrade is null ? Sites : [.. Sites, TonnageBandUpgrade];

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
}

/// <summary>PATCH body: a null property leaves the stored answer unchanged.</summary>
public class PatchChangeRequestSiteRequest
{
    public string? Country { get; set; }
    public string? Coordinates { get; set; }
    public List<string>? OperationCodes { get; set; }
    public bool? ConditionsOfExport { get; set; }
}
