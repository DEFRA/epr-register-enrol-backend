using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GlassRecyclingProcess
{
    [JsonStringEnumMemberName("glass_re_melt")]
    Remelt,

    [JsonStringEnumMemberName("glass_other")]
    Other,
}
