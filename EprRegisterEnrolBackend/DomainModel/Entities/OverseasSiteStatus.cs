using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverseasSiteStatus
{
    Proposed,
    Active,
    Discarded,
}
