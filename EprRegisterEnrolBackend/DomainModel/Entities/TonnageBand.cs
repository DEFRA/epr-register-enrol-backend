using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// ReEx's <c>TONNAGE_BAND</c> values (D2). Stored as ReEx's own strings, such as <c>up_to_500</c>, by
/// <see cref="TonnageBandSerializer"/>, so every property of this type needs that serializer.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TonnageBand
{
    UpTo500,
    UpTo5000,
    UpTo10000,
    Over10000,
}
