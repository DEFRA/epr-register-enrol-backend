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
/// </summary>
public class AccreditationChangeRequest
{
    public const string AddOverseasSiteChangeType = "add-overseas-site";

    [BsonId]
    public string RequestId { get; set; } = Guid.NewGuid().ToString();

    public string ChangeType { get; set; } = AddOverseasSiteChangeType;

    public required string OrganisationId { get; set; }

    /// <summary>The Approved accreditation application this request changes.</summary>
    public required string ApplicationId { get; set; }

    public string? RegistrationId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public required MaterialType MaterialType { get; set; }

    public required int Year { get; set; }

    public string? AccreditationReference { get; set; }

    [BsonRepresentation(BsonType.String)]
    public ChangeRequestStatus Status { get; set; } = ChangeRequestStatus.InProgress;

    public List<ChangeRequestSite> Sites { get; set; } = [];

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
}

public class ChangeRequestSite
{
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
