namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// The one-line address an overseas site keeps beside its parts: its first line, town or city and country, as the
/// operator's own ORS journey builds it.
/// </summary>
public static class OverseasSiteAddress
{
    public static string Format(string? addressLine1, string? townOrCity, string? country) =>
        string.Join(", ", new[] { addressLine1, townOrCity, country }.Where(p => !string.IsNullOrWhiteSpace(p)));
}
