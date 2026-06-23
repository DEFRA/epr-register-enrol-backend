using System.Text.Json;

namespace EprRegisterEnrolBackend.ReEx.Dtos;

/// <summary>
/// Top-level keyed map returned by GET …/overseas-sites.
/// JSON shape: { "100": { name, country, address, coordinates, validFrom }, … }
/// </summary>
public class OverseasSitesDto : Dictionary<string, OverseasSiteDto> { }

public class OverseasSiteDto
{
    public string? Name { get; init; }
    public string? Country { get; init; }
    public OverseasSiteAddressDto? Address { get; init; }
    // Null in current samples; retained as raw JSON in case the shape is filled later
    public JsonElement? Coordinates { get; init; }
    public string? ValidFrom { get; init; }
}

public class OverseasSiteAddressDto
{
    public string? Line1 { get; init; }
    public string? TownOrCity { get; init; }
}
