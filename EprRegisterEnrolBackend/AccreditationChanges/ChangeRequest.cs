using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.AccreditationChanges;

public sealed record AccreditationChangeTarget(string OrganisationId, string RegistrationId,
    string AccreditationId, MaterialType MaterialType, int Year);

public sealed class ChangeRequest
{
    [BsonId]
    public string RequestId { get; set; } = Guid.NewGuid().ToString();
    public int SchemaVersion { get; set; } = 1;
    public string ChangeType { get; set; } = "add-overseas-site";
    public required AccreditationChangeTarget Target { get; set; }
    public required string OrganisationName { get; set; }
    public required string RegistrationReference { get; set; }
    public required List<OverseasSiteModel> BaselineSites { get; set; }
    public required PreparedOverseasSite Proposal { get; set; }
    public string ProposedSiteId { get; set; } = Guid.NewGuid().ToString();
    public string Status { get; set; } = "submission-pending";
    public bool Active { get; set; } = true;
    public string? CaseId { get; set; }
    public string? CaseReference { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    [BsonIgnore]
    public string? CaseState { get; set; }
}

public sealed class FixedAccreditationView
{
    public required string OrganisationId { get; init; }
    public required string RegistrationId { get; init; }
    public required string AccreditationId { get; init; }
    public MaterialType MaterialType { get; init; }
    public int Year { get; init; }
    public string Status => "Granted";
    public required string OrganisationName { get; init; }
    public required string RegistrationReference { get; init; }
    public required List<OverseasSiteModel> OverseasSites { get; init; }
    public required PreparedOverseasSite Proposal { get; init; }
    public ChangeRequest? ActiveRequest { get; init; }
    public List<ChangeRequest> History { get; init; } = [];
}
