using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterialType
{
    Steel,
    Wood,
    Aluminium,
    Fibre,
    Glass,
    Paper,
    Plastic
}
