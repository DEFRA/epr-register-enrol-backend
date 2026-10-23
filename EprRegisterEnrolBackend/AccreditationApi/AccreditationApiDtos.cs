using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.AccreditationApi;

// The wire shapes of the read-only accreditation API for service clients such as ReEx. Field
// names follow the v2 draft OpenAPI spec (docs/openapi/registration-accreditation-api.v2.yaml),
// which is under review (RA-644) and will change. camelCase on the wire.
//
// Fields the spec marks required are always sent. Optional fields this backend does not hold
// are left out rather than sent as null, so what is present is what is known.

/// <summary>
/// The v2 <c>AccreditationSummary</c>: the accreditation without history or signatories. Returned
/// by search, and the base of <see cref="AccreditationDto"/>.
/// </summary>
public record AccreditationSummaryDto
{
    public required int OrganisationNumber { get; init; }

    public required string RegistrationNumber { get; init; }

    public required string AccreditationNumber { get; init; }

    public required int AccreditationYear { get; init; }

    /// <summary>The material as held: Steel, Wood, Aluminium, Fibre, Glass, Paper or Plastic.</summary>
    public required string Material { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GlassRecyclingProcess { get; init; }

    /// <summary><c>reprocessor</c> or <c>exporter</c>.</summary>
    public required string WasteProcessingType { get; init; }

    /// <summary>
    /// <c>approved</c>, <c>suspended</c> or <c>cancelled</c>: the latest status history entry
    /// whose effective date is on or before today.
    /// </summary>
    public required string Status { get; init; }

    /// <summary><c>up_to_500</c>, <c>up_to_5000</c>, <c>up_to_10000</c> or <c>over_10000</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TonnageBand { get; init; }
}

/// <summary>
/// The v2 <c>Accreditation</c>: a summary plus its histories (oldest first) and PRN signatories.
/// Returned only when getting one accreditation.
/// </summary>
public sealed record AccreditationDto : AccreditationSummaryDto
{
    public required IReadOnlyList<SignatoryDto> Signatories { get; init; }

    public required IReadOnlyList<StatusHistoryEntryDto> StatusHistory { get; init; }

    public required IReadOnlyList<TonnageBandHistoryEntryDto> TonnageBandHistory { get; init; }
}

public sealed record SignatoryDto
{
    public required string FullName { get; init; }

    public required string Email { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phone { get; init; }
}

public sealed record StatusHistoryEntryDto
{
    public required string Status { get; init; }

    /// <summary>When the status takes effect (UK date), serialised as <c>yyyy-MM-dd</c>.</summary>
    public required DateOnly EffectiveDate { get; init; }

    public required DateTime RecordedAt { get; init; }
}

public sealed record TonnageBandHistoryEntryDto
{
    public required string TonnageBand { get; init; }

    public required DateTime RecordedAt { get; init; }
}

public sealed record AddressDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Line1 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Line2 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Town { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? County { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Country { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Postcode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Region { get; init; }
}

public sealed record ContactDetailsDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FullName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phone { get; init; }
}

/// <summary>
/// An interim site, returned inside its overseas site. <c>removedAt</c> set means withdrawn.
/// </summary>
public sealed record InterimSiteDto
{
    public required int SiteId { get; init; }

    public required string SiteNumber { get; init; }

    public required string SiteName { get; init; }

    public required AddressDto Address { get; init; }

    public required ContactDetailsDto Contact { get; init; }

    public required IReadOnlyList<string> OperationCodes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? RemovedAt { get; init; }
}

/// <summary>
/// An overseas site as it stands on an accreditation, with its own status history and its
/// interim sites. BES evidence is not served.
/// </summary>
public sealed record AccreditedOverseasSiteDto
{
    public required int SiteId { get; init; }

    public string? OrsId { get; init; }

    public required string SiteName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FormattedAddress { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AddressDto? Address { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Coordinates { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContactDetailsDto? Contact { get; init; }

    public required IReadOnlyList<string> OperationCodes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepatriatedLoads { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ConditionsOfExport { get; init; }

    public required bool IsEu { get; init; }

    public required bool IsOecd { get; init; }

    /// <summary>The date the site was approved onto the accreditation (ReEx ORS-A approval).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ValidFrom { get; init; }

    public required string Status { get; init; }

    public required IReadOnlyList<StatusHistoryEntryDto> StatusHistory { get; init; }

    /// <summary>Active interim sites; withdrawn ones too when <c>includeWithdrawn=true</c>.</summary>
    public required IReadOnlyList<InterimSiteDto> InterimSites { get; init; }
}

public sealed record PageDto<T>
{
    public required int PageNumber { get; init; }

    public required int Limit { get; init; }

    public required int TotalItems { get; init; }

    public required IReadOnlyList<T> Items { get; init; }
}
