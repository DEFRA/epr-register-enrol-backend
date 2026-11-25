using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// How a push from the Case Management service lands on the application's
/// <see cref="ApplicationStatus"/> (RA-368 §4.3). The status and query push receivers in
/// <c>AccreditationApplicationEndpoints</c> apply it, and the admin API serves it (RA-751,
/// <c>GET /admin/case-management-status-mapping</c>), so the Admin UI compares a work item with its
/// application by the table the receivers use, never a copy of it.
/// </summary>
public static class CaseManagementStatusMapping
{
    /// <summary>
    /// Raw Case Management state id (the status push's <c>toStateId</c>) to the status it sets, in
    /// the order the admin API lists them. A state with no entry (anything the Case Management
    /// service adds in future) is a deliberate no-op for <see cref="ApplicationStatus"/>: the push
    /// still updates <c>CaseManagementStatusUpdatedAt</c> for ordering. "queried" and "withdrawn"
    /// are never sent as a status push: a query has its own richer push
    /// (<see cref="QueryPushStatus"/>), and withdrawal is out of scope for the push (§4.1, §4.5).
    /// <para>
    /// "assessment-started" (RA-527 follow-up) is the Case Management service's internal "started
    /// but still Duly made" state. Its start step is excluded from pushes, so it is never sent by
    /// design; the entry is a safeguard so a misconfigured or replayed push lands on DulyMade rather
    /// than Updated or a status no-op. Only a query response or a deadline change makes a started
    /// case Updated.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, ApplicationStatus>> StatusPush =
    [
        new("submitted", ApplicationStatus.Submitted),
        new("duly-made", ApplicationStatus.DulyMade),
        new("assessment-started", ApplicationStatus.DulyMade),
        new("assessment-in-progress", ApplicationStatus.Updated),
        new("updated", ApplicationStatus.Updated),
        new("awaiting-decision", ApplicationStatus.AwaitingDecision),
        new("approved", ApplicationStatus.Approved),
        new("rejected", ApplicationStatus.Rejected),
    ];

    // Throws at type initialisation if a state id is listed twice.
    private static readonly Dictionary<string, ApplicationStatus> ByStateId = new(
        StatusPush,
        StringComparer.Ordinal
    );

    /// <summary>The status a query push (<c>/case-management/{workItemId}/query</c>) sets.</summary>
    public const ApplicationStatus QueryPushStatus = ApplicationStatus.Queried;

    /// <summary>The status a status push to <paramref name="toStateId"/> sets, or null for none.</summary>
    public static ApplicationStatus? ForStatusPush(string toStateId) =>
        ByStateId.TryGetValue(toStateId, out var status) ? status : null;
}
