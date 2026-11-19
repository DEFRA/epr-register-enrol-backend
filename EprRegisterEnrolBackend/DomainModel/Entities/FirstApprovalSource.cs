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
    /// An in-year change request granted in this service (RA-495), from its effective date (AC6). Kept apart from
    /// <see cref="ThisService"/> to say how the site was first approved; it is not served as validFrom. The grant's
    /// date is served from the granted Accreditation's entry for the site, to that accreditation's application only
    /// (<see cref="AccreditationOverseasSiteEntry.AccreditedFrom"/>, 3bb.43, 3bb.44).
    /// </summary>
    ChangeRequest,
}
