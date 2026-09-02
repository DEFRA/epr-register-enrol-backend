namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

/// <summary>
/// RA-526: maps a ReEx regulator code (as carried on
/// <see cref="EprRegisterEnrolBackend.ReEx.Dtos.RegistrationBaseDto.SubmittedToRegulator"/>) to
/// a <see cref="Nation"/>. This replaces postcode-derived nation lookup as the source of
/// Regulator Nation - see Nation.cs.
/// </summary>
public static class RegulatorNationMapper
{
    private static readonly Dictionary<string, Nation> CodeToNation = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["ea"] = Nation.England,
        ["nrw"] = Nation.Wales,
        ["sepa"] = Nation.Scotland,
        ["niea"] = Nation.NorthernIreland,
    };

    /// <summary>
    /// Maps a regulator code to a Nation, defaulting to England when the code is null/blank
    /// (most test registrations don't set submittedToRegulator yet) or unrecognised. Returns
    /// false only for the unrecognised-non-null case, so the caller can log a warning and keep
    /// the gap observable - null/blank is treated as the expected default, not a data gap.
    /// </summary>
    public static bool TryMap(string? regulatorCode, out Nation nation)
    {
        if (string.IsNullOrWhiteSpace(regulatorCode))
        {
            nation = Nation.England;
            return true;
        }

        if (CodeToNation.TryGetValue(regulatorCode, out var mapped))
        {
            nation = mapped;
            return true;
        }

        nation = Nation.England;
        return false;
    }
}
