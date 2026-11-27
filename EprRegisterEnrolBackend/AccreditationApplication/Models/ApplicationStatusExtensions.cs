namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

/// <summary>
/// Which statuses end an application: Approved, Rejected and Withdrawn. Every other status is in flight. The
/// migration, the model sync and the registration-number rules all use this one definition.
/// </summary>
public static class ApplicationStatusExtensions
{
    private static readonly ApplicationStatus[] s_closed =
    [
        ApplicationStatus.Approved,
        ApplicationStatus.Rejected,
        ApplicationStatus.Withdrawn,
    ];

    // RA-357 / RA-765: a withdrawn or refused (Rejected) application no longer counts as the application for its
    // year, so the operator can start a new one. Every other status, Approved included, is live.
    private static readonly ApplicationStatus[] s_notLive =
    [
        ApplicationStatus.Rejected,
        ApplicationStatus.Withdrawn,
    ];

    /// <summary>The statuses that end an application, for a query filter.</summary>
    public static IReadOnlyList<ApplicationStatus> Closed => s_closed;

    /// <summary>The statuses that do not block a new application for the same year, for a query filter.</summary>
    public static IReadOnlyList<ApplicationStatus> NotLive => s_notLive;

    public static bool IsInFlight(this ApplicationStatus status) => !s_closed.Contains(status);

    /// <summary>
    /// Whether an application in this status is the live one for its year: anything not withdrawn or refused.
    /// </summary>
    public static bool IsLive(this ApplicationStatus status) => !s_notLive.Contains(status);
}
