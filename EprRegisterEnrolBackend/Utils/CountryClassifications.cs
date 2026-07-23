namespace EprRegisterEnrolBackend.Utils;

public static class CountryClassifications
{
    private static readonly HashSet<string> EuCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "Austria",
        "Belgium",
        "Bulgaria",
        "Croatia",
        "Cyprus",
        "Czechia",
        "Czech Republic",
        "Denmark",
        "Estonia",
        "Finland",
        "France",
        "Germany",
        "Greece",
        "Hungary",
        "Ireland",
        "Italy",
        "Latvia",
        "Lithuania",
        "Luxembourg",
        "Malta",
        "Netherlands",
        "Poland",
        "Portugal",
        "Romania",
        "Slovakia",
        "Slovenia",
        "Spain",
        "Sweden",
    };

    private static readonly HashSet<string> OecdCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        // EU members that are also OECD members (Bulgaria, Croatia, Cyprus, Malta, Romania are EU but NOT OECD)
        "Austria",
        "Belgium",
        "Czechia",
        "Czech Republic",
        "Denmark",
        "Estonia",
        "Finland",
        "France",
        "Germany",
        "Greece",
        "Hungary",
        "Ireland",
        "Italy",
        "Latvia",
        "Lithuania",
        "Luxembourg",
        "Netherlands",
        "Poland",
        "Portugal",
        "Slovakia",
        "Slovenia",
        "Spain",
        "Sweden",
        // Non-EU OECD
        "Australia",
        "Canada",
        "Chile",
        "Colombia",
        "Costa Rica",
        "Iceland",
        "Israel",
        "Japan",
        "South Korea",
        "Korea",
        "Mexico",
        "New Zealand",
        "Norway",
        "Switzerland",
        "Türkiye",
        "Turkey",
        "United Kingdom",
        "United States",
        "United States of America",
    };

    public static bool IsEu(string? country) =>
        country is not null && EuCountries.Contains(country);

    public static bool IsOecd(string? country) =>
        country is not null && OecdCountries.Contains(country);
}
