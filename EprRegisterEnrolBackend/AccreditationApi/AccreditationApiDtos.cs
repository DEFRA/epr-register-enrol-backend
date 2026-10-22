using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.AccreditationApi;

// The wire shapes of the read-only accreditation API for service clients such as ReEx. Field
// names follow the draft OpenAPI spec (docs/openapi/registration-accreditation-api.yaml), which
// is under review (RA-644) and will change. camelCase on the wire.
//
// Fields the spec marks required are always sent. Optional fields this backend does not hold
// are left out rather than sent as null, so what is present is what is known.

public sealed record AccreditationDto
{
    public required string AccreditationReference { get; init; }

    public required string RegistrationNumber { get; init; }

    public required int Year { get; init; }

    /// <summary>A granted accreditation is always <c>approved</c>.</summary>
    public required string Status { get; init; }

    /// <summary>The material as held: Steel, Wood, Aluminium, Fibre, Glass, Paper or Plastic.</summary>
    public required string Material { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GlassRecyclingProcess { get; init; }

    /// <summary><c>reprocessor</c> or <c>exporter</c>.</summary>
    public required string WasteProcessingType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AddressDto? SiteAddress { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrnIssuanceDto? PrnIssuance { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<BusinessPlanItemDto>? BusinessPlan { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContactDetailsDto? SubmitterContactDetails { get; init; }

    /// <summary>The ORS ids on this accreditation; exporters only. See the overseas-sites route.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? OverseasSites { get; init; }
}

public sealed record AddressDto
{
    /// <summary>
    /// The address as one string, which is all that is held for a UK site address. A UK address is
    /// held in parts only by ReEx.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FullAddress { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Line1 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Line2 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Town { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Country { get; init; }
}

public sealed record PrnIssuanceDto
{
    /// <summary>
    /// The planned tonnage band: UpTo500, UpTo5000, UpTo10000 or Over10000. It is the operator's
    /// plan from the application, which this backend holds, not a separately approved band.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlannedIssuance { get; init; }

    public required IReadOnlyList<ContactDetailsDto> Signatories { get; init; }
}

public sealed record BusinessPlanItemDto
{
    public required string Description { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DetailedDescription { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PercentSpent { get; init; }
}

public sealed record ContactDetailsDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FullName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phone { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }
}

/// <summary>
/// An overseas site as it stands on an accreditation. Contact details and BES evidence are not
/// served yet.
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

    public required IReadOnlyList<string> OperationCodes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code1 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code2 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code3 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepatriatedLoads { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ConditionsOfExport { get; init; }

    public required bool IsEu { get; init; }

    public required bool IsOecd { get; init; }

    /// <summary>The <c>siteId</c> of each interim site that has not been withdrawn.</summary>
    public required IReadOnlyList<int> InterimSiteIds { get; init; }

    /// <summary>
    /// The date ReEx recorded the site's approval (ORS-A), as a date-time string. Absent means
    /// the site is registered but not yet accredited.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValidFrom { get; init; }

    /// <summary>Included in (approved for) the accreditation.</summary>
    public required bool Selected { get; init; }

    public required bool RegisteredNowAccredited { get; init; }

    public required bool IsNewSite { get; init; }
}

public sealed record PageDto<T>
{
    public required int PageNumber { get; init; }

    public required int Limit { get; init; }

    public required int TotalItems { get; init; }

    public required IReadOnlyList<T> Items { get; init; }
}
