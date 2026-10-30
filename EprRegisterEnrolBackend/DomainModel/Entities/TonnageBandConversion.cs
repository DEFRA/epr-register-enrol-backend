using System.Reflection;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// Converts to and from <see cref="TonnageBand"/>: from the legacy application's <see cref="PlannedTonnageBand"/>
/// (stored as an int32), and to and from ReEx's band strings, which are what the model stores (D2).
/// </summary>
public static class TonnageBandConversion
{
    // The names come from the enum's JSON names, so Mongo and JSON can never disagree.
    private static readonly Dictionary<TonnageBand, string> s_toReEx = Enum.GetValues<TonnageBand>()
        .ToDictionary(
            band => band,
            band =>
                typeof(TonnageBand)
                    .GetField(band.ToString())!
                    .GetCustomAttribute<JsonStringEnumMemberNameAttribute>()!
                    .Name
        );

    private static readonly Dictionary<string, TonnageBand> s_fromReEx = s_toReEx.ToDictionary(
        p => p.Value,
        p => p.Key
    );

    /// <summary>The band the legacy value means, or null when none was chosen.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The stored int32 is not a defined band.</exception>
    public static TonnageBand? FromLegacy(PlannedTonnageBand? legacy) =>
        legacy switch
        {
            null => null,
            PlannedTonnageBand.UpTo500 => TonnageBand.UpTo500,
            PlannedTonnageBand.UpTo5000 => TonnageBand.UpTo5000,
            PlannedTonnageBand.UpTo10000 => TonnageBand.UpTo10000,
            PlannedTonnageBand.Over10000 => TonnageBand.Over10000,
            _ => throw new ArgumentOutOfRangeException(
                nameof(legacy),
                legacy,
                "Not a planned tonnage band."
            ),
        };

    /// <summary>ReEx's string for the band, such as <c>up_to_500</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined band.</exception>
    public static string ToReExName(TonnageBand band) =>
        s_toReEx.TryGetValue(band, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(band), band, "Not a tonnage band.");

    public static bool TryFromReExName(string name, out TonnageBand band) =>
        s_fromReEx.TryGetValue(name, out band);
}
