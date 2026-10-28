using EprRegisterEnrolBackend.DomainModel.Entities;

namespace EprRegisterEnrolBackend.DomainModel.Status;

/// <summary>The two overseas site lifecycles in design §3.4.</summary>
public static class OverseasSiteTransitions
{
    /// <summary>
    /// The ORS within an Accreditation (<c>accreditationOverseasSites[].status</c>), independent of the
    /// accreditation's own status (A2).
    /// </summary>
    public static StatusTransitions<AccreditedSiteStatus> Accredited { get; } =
        new(
            "accredited overseas site",
            new Dictionary<AccreditedSiteStatus, AccreditedSiteStatus[]>
            {
                [AccreditedSiteStatus.Approved] = [AccreditedSiteStatus.Suspended],
                [AccreditedSiteStatus.Suspended] = [AccreditedSiteStatus.Approved],
            }
        );

    /// <summary>
    /// The ORS on the Registration. A site added in an application is Proposed until the application is
    /// approved (Active) or rejected or withdrawn (Discarded).
    /// </summary>
    public static StatusTransitions<OverseasSiteStatus> Registered { get; } =
        new(
            "overseas site",
            new Dictionary<OverseasSiteStatus, OverseasSiteStatus[]>
            {
                [OverseasSiteStatus.Proposed] =
                [
                    OverseasSiteStatus.Active,
                    OverseasSiteStatus.Discarded,
                ],
            }
        );
}
