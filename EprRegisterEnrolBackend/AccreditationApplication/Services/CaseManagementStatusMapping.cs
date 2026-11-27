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
    /// the order the admin API lists them. A push to a state with no entry still sets no status
    /// (RA-368 §4.3): it only updates <c>CaseManagementStatusUpdatedAt</c> for ordering. Unless the
    /// state is in <see cref="KnownIgnoredStateIds"/>, it is also logged at Error
    /// (<see cref="LogUnknownState"/>), because only a state Case Management added without this
    /// table being told lands there. Withdrawal is out of scope for the push (§4.1, §4.5), so
    /// "withdrawn" is never sent (management-be <c>StandardStatusPushHook</c> excludes it).
    /// <para>
    /// "assessment-started" (RA-527 follow-up) is the Case Management service's internal "started
    /// but still Duly made" state. Its start step is excluded from pushes, so it is never sent by
    /// design; the entry is a safeguard so a misconfigured or replayed push lands on DulyMade rather
    /// than Updated or a status no-op. Only a query response or a deadline change makes a started
    /// case Updated.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, ApplicationStatus>> StatusPushMap =
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
        StatusPushMap,
        StringComparer.Ordinal
    );

    /// <summary>
    /// States Case Management pushes in normal traffic that deliberately set no status, so a push to
    /// one is not logged as unknown. "queried": a deadline change on a queried item (management-be
    /// <c>SlaService.ExtendAsync</c>, the StandardWorkflow <c>sla-extend</c> self-loop
    /// queried → queried) is pushed with the item still queried, because
    /// <c>StandardStatusPushHook</c> never excludes a self-loop (RA-351). The query itself has its
    /// own richer push (<see cref="QueryPushStatus"/>), which is what sets Queried; the extend
    /// changes no status here.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownIgnoredStateIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "queried",
    };

    /// <summary>The status a query push (<c>/case-management/{workItemId}/query</c>) sets.</summary>
    public const ApplicationStatus QueryPushStatus = ApplicationStatus.Queried;

    /// <summary>
    /// The 400 <c>detail</c> for a status push that names no <c>toStateId</c>. Both push receivers
    /// (the accreditation application's and the change request item's) answer it.
    /// </summary>
    public const string MissingToStateIdDetail = "A status push must name the toStateId the work item moved to.";

    /// <summary>
    /// The status a status push to <paramref name="toStateId"/> sets, or null for a state with no
    /// entry. A blank <paramref name="toStateId"/> is not "no entry": the receivers refuse it first
    /// (<see cref="RejectMissingToStateId"/>), so reaching here with one is a bug and throws.
    /// </summary>
    public static ApplicationStatus? ForStatusPush(string toStateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toStateId);
        return ByStateId.TryGetValue(toStateId, out var status) ? status : null;
    }

    /// <summary>
    /// A 400 ProblemDetails, logged at Error, when a status push names no <c>toStateId</c>; null
    /// when it names one. <c>toStateId</c> is a required string, but an explicit JSON
    /// <c>null</c> (or an empty string) still binds, and acting on it would advance the ordering
    /// watermark for a move nobody can name. Case Management's push adapter
    /// (management-be <c>HttpOperatorBackendPushAdapter</c>) never retries a 4xx: it logs the
    /// response at Error, and a decision push that fails abandons the caseworker's action with no
    /// state changed.
    /// </summary>
    internal static IResult? RejectMissingToStateId(
        ILogger logger,
        string operation,
        Guid workItemId,
        StatusChangedFromCaseManagementRequest request,
        string? correlationId
    )
    {
        if (!string.IsNullOrWhiteSpace(request.ToStateId))
            return null;

        logger.LogError(
            "{Operation}: status push has no toStateId, refused with 400, for workItemId={WorkItemId} actionId={ActionId} occurredAt={OccurredAt} correlationId={CorrelationId}",
            operation,
            workItemId,
            request.ActionId,
            request.OccurredAt,
            correlationId ?? "(absent)"
        );
        return Results.Problem(detail: MissingToStateIdDetail, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Logs at Error a status push to a state that is neither in <see cref="StatusPushMap"/> nor in
    /// <see cref="KnownIgnoredStateIds"/>: Case Management has a state this service was never told
    /// about. The push is still answered and applied as before (no status change, ordering only);
    /// this only makes it visible. Call after <see cref="RejectMissingToStateId"/>.
    /// </summary>
    internal static void LogUnknownState(
        ILogger logger,
        string operation,
        Guid workItemId,
        StatusChangedFromCaseManagementRequest request,
        string? correlationId
    )
    {
        if (ByStateId.ContainsKey(request.ToStateId) || KnownIgnoredStateIds.Contains(request.ToStateId))
            return;

        logger.LogError(
            "{Operation}: status push to unknown toStateId={ToStateId} sets no status, for workItemId={WorkItemId} actionId={ActionId} occurredAt={OccurredAt} correlationId={CorrelationId}",
            operation,
            request.ToStateId,
            workItemId,
            request.ActionId,
            request.OccurredAt,
            correlationId ?? "(absent)"
        );
    }
}
