using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccreditedSiteStatus
{
    Approved,
    Suspended,
}
