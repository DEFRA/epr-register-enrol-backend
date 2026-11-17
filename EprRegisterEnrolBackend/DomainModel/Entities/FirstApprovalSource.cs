using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>Where an overseas site's first approved date came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FirstApprovalSource
{
    /// <summary>ReEx's approval date for the site.</summary>
    ReEx,

    /// <summary>
    /// An application's approval in this service, for a site ReEx gave no date for. Legacy never writes it onto the
    /// application's site, so it is not served as validFrom.
    /// </summary>
    ThisService,

    /// <summary>
    /// An in-year change request granted in this service (RA-495), from the request's submission date. Legacy writes
    /// it onto the granted site as its validFrom, so it is served as one (3bb.28).
    /// </summary>
    ChangeRequest,
}
