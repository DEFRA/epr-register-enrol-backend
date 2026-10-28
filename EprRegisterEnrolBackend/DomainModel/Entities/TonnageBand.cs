using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// ReEx's <c>TONNAGE_BAND</c> values (D2). Stored as ReEx's own strings, such as <c>up_to_500</c>, by
/// <see cref="TonnageBandSerializer"/>, so every property of this type needs that serializer. JSON uses the
/// same strings.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TonnageBand
{
    [JsonStringEnumMemberName("up_to_500")]
    UpTo500,

    [JsonStringEnumMemberName("up_to_5000")]
    UpTo5000,

    [JsonStringEnumMemberName("up_to_10000")]
    UpTo10000,

    [JsonStringEnumMemberName("over_10000")]
    Over10000,
}
