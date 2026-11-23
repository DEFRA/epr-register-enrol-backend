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
    /// The latest entry in the accreditation's <c>statusHistory</c>, the same entry and the same shape:
    /// the latest <c>effectiveFromDate</c>, and the later <c>recordedAt</c> where two share one. It is
    /// the latest whether or not its <c>effectiveFromDate</c> has arrived, so use its dates to tell
    /// whether it is in effect.
    /// </summary>
    public required StatusHistoryEntryDto LatestStatus { get; init; }

    /// <summary><c>up_to_500</c>, <c>up_to_5000</c>, <c>up_to_10000</c> or <c>over_10000</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TonnageBand { get; init; }
}

/// <summary>
/// The v2 <c>Accreditation</c>: a summary plus its histories (oldest first), PRN signatories and,
/// for an exporter, every overseas site on the accreditation with its interim sites and status
/// history (RA-647: one request returns the complete accreditation). Returned only when getting one
/// accreditation.
/// </summary>
public sealed record AccreditationDto : AccreditationSummaryDto
{
    public required IReadOnlyList<SignatoryDto> Signatories { get; init; }

    public required IReadOnlyList<StatusHistoryEntryDto> StatusHistory { get; init; }

    public required IReadOnlyList<TonnageBandHistoryEntryDto> TonnageBandHistory { get; init; }

    /// <summary>
    /// Exporters only, ordered by ORS id: a reprocessor has a site, not overseas sites, so for a
    /// reprocessor the property is left out.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AccreditedOverseasSiteDto>? OverseasSites { get; init; }
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

    /// <summary>
    /// The first day the status applies (UK date, <c>yyyy-MM-dd</c>): the later of 1 January of the
    /// accreditation year and the UK date the change was recorded. May be in the future.
    /// </summary>
    public required DateOnly EffectiveFromDate { get; init; }

    /// <summary>
    /// The last day the status applies: the day before the next entry's
    /// <c>effectiveFromDate</c>. Always present in the response, and <c>null</c> on the latest entry.
    /// </summary>
    public required DateOnly? EffectiveToDate { get; init; }

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
/// An interim site, returned inside its overseas site, withdrawn ones included. It is
/// <c>approved</c> from when its overseas site was approved and <c>withdrawn</c> from when it was
/// withdrawn, as its <c>statusHistory</c> shows.
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

    /// <summary>The latest entry in the interim site's <c>statusHistory</c>: see the accreditation's.</summary>
    public required StatusHistoryEntryDto LatestStatus { get; init; }

    public required IReadOnlyList<StatusHistoryEntryDto> StatusHistory { get; init; }
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

    /// <summary>The latest entry in the site's <c>statusHistory</c>: see the accreditation's.</summary>
    public required StatusHistoryEntryDto LatestStatus { get; init; }

    public required IReadOnlyList<StatusHistoryEntryDto> StatusHistory { get; init; }

    /// <summary>Every interim site on the site, withdrawn ones included.</summary>
    public required IReadOnlyList<InterimSiteDto> InterimSites { get; init; }
}

public sealed record PageDto<T>
{
    public required int PageNumber { get; init; }

    public required int Limit { get; init; }

    public required int TotalItems { get; init; }

    public required IReadOnlyList<T> Items { get; init; }
}

/// <summary>
/// A registration, as this backend holds it: every application for it, of any status and year.
/// This backend holds no registration record, so the applications are all there is to serve.
/// </summary>
public sealed record RegistrationDto
{
    public required int OrganisationNumber { get; init; }

    public required string RegistrationNumber { get; init; }

    /// <summary>Ordered by year.</summary>
    public required IReadOnlyList<RegistrationApplicationDto> Applications { get; init; }
}

/// <summary>One application for a registration, with every overseas site on it.</summary>
public sealed record RegistrationApplicationDto
{
    public required int Year { get; init; }

    /// <summary>The application's status, such as <c>Saved</c>, <c>Submitted</c> or <c>Approved</c>.</summary>
    public required string ApplicationStatus { get; init; }

    /// <summary>Present once the application holds an accreditation number.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccreditationNumber { get; init; }

    /// <summary>
    /// Every overseas site on the application, whether or not it is selected. Ordered by ORS id, with
    /// sites that have none first. Empty for a reprocessor.
    /// </summary>
    public required IReadOnlyList<RegistrationOverseasSiteDto> OverseasSites { get; init; }
}

/// <summary>An overseas site as it stands on an application.</summary>
public sealed record RegistrationOverseasSiteDto
{
    public required int SiteId { get; init; }

    /// <summary>The site's ORS id. Always present, and <c>null</c> until one is issued.</summary>
    public required string? OrsId { get; init; }

    public required string SiteName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FormattedAddress { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AddressDto? Address { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Coordinates { get; init; }

    /// <summary>
    /// Whether the site is selected on the application. On an application that is not yet approved
    /// this is the operator's choice, not an accredited site.
    /// </summary>
    public required bool Selected { get; init; }
}
