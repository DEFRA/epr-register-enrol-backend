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
    /// Maps a regulator code to a Nation. Returns false, with no nation, for a null, blank or
    /// unrecognised code: <paramref name="nation"/> is meaningless then.
    ///
    /// RA-000: there is no default. ReEx never stores a registration without a regulator code -
    /// epr-backend's <c>src/repositories/organisations/schema/registration.js</c> has
    /// <c>submittedToRegulator: Joi.string().valid(EA, NRW, SEPA, NIEA).required()</c> - so a
    /// missing or unknown code from ReEx is a ReEx defect, and callers fail loudly on it. (The
    /// England default this replaced came from RA-526, for this service's own test data; the
    /// Development stub keeps that as its own fixture default.)
    /// </summary>
    public static bool TryMap(string? regulatorCode, out Nation nation)
    {
        if (regulatorCode is not null && CodeToNation.TryGetValue(regulatorCode, out var mapped))
        {
            nation = mapped;
            return true;
        }

        nation = default;
        return false;
    }
}
