using System.Globalization;
using System.Text.RegularExpressions;

namespace EprRegisterEnrolBackend.Utils;

// RA-479: the frontend hint promises latitude/longitude to at least 4 decimal places
// (~11m accuracy), so the backend enforces the same shape as a defence-in-depth check
// rather than trusting the frontend to have validated it. Single source of truth shared
// by AddOverseasSiteRequestValidator and PromoteOverseasSiteRequestValidator — both
// endpoints can persist Coordinates onto the same site, so both must enforce the rule.
public static class CoordinatesValidation
{
    // S6444: Coordinates arrives straight off a client request body, so the match is given
    // an explicit timeout rather than being left to run unbounded on the request thread.
    // Callers must cap length (MaximumLength) ahead of this with CascadeMode.Stop so the
    // regex never sees unbounded input.
    public static readonly Regex FormatRegex = new(
        @"^-?\d+\.\d{4,}\s*,\s*-?\d+\.\d{4,}$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100)
    );

    public static bool IsWithinRange(string coordinates)
    {
        var parts = coordinates.Split(',');
        var latitude = double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
        var longitude = double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
        return latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
    }
}
