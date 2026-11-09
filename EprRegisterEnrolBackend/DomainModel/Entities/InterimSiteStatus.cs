using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InterimSiteStatus
{
    Proposed,
    Active,
    Withdrawn,
    Discarded,
}
