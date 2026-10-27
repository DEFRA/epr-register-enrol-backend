using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>A structured postal address (design doc §3.2). Used by every entity that holds one.</summary>
public class PostalAddress
{
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? TownOrCity { get; set; }
    public string? Region { get; set; }
    public string? Postcode { get; set; }
    public string? Country { get; set; }
}

public class ContactPerson
{
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? JobTitle { get; set; }
}

/// <summary>Where an entity's data came from, and whether it still needs enriching from ReEx (Q35: deferred).</summary>
public class EntitySource
{
    public string System { get; set; } = "Application";
    public DateTime? ImportedAt { get; set; }
    public string? ReExSchemaVersion { get; set; }
    public bool EnrichmentPending { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RegistrationStatus
{
    Created,
    Approved,
    Rejected,
    Cancelled,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WasteProcessingKind
{
    Reprocessor,
    Exporter,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverseasSiteStatus
{
    Proposed,
    Active,
    Discarded,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SiteOrigin
{
    ReEx,
    Operator,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InterimSiteStatus
{
    Proposed,
    Active,
    Withdrawn,
    Discarded,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccreditationStatus
{
    Approved,
    Suspended,
    Cancelled,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccreditedSiteStatus
{
    Approved,
    Suspended,
}

/// <summary>ReEx's <c>TONNAGE_BAND</c> values, stored as the strings ReEx uses (D2).</summary>
public static class TonnageBands
{
    public const string UpTo500 = "up_to_500";
    public const string UpTo5000 = "up_to_5000";
    public const string UpTo10000 = "up_to_10000";
    public const string Over10000 = "over_10000";

    public static readonly IReadOnlyList<string> All = [UpTo500, UpTo5000, UpTo10000, Over10000];
}
