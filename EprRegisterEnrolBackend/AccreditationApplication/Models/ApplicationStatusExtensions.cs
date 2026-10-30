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

    /// <summary>The statuses that end an application, for a query filter.</summary>
    public static IReadOnlyList<ApplicationStatus> Closed => s_closed;

    public static bool IsInFlight(this ApplicationStatus status) => !s_closed.Contains(status);
}
