using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>Where an overseas site's first approved date came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FirstApprovalSource
{
    /// <summary>ReEx's approval date for the site.</summary>
    ReEx,

    /// <summary>An approval in this service, for a site ReEx gave no date for.</summary>
    ThisService,
}
