using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Auth;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-495: Case Management pushes each add-ORS work item's status changes to the change request item it was raised
/// for. The same caller, auth scheme, body and guards as the accreditation application's case-management status
/// route (<c>AccreditationApplicationEndpoints.StatusChangedFromCaseManagement</c>), applied to one item rather than
/// a whole application.
/// </summary>
public static class ChangeRequestStatusEndpoints
{
    internal const string Route = "api/v1/accreditation-changes/case-management/{workItemId}/status";

    // Sibling items share their request's document, so pushes for them can race on its version. Each lost race is
    // read again; past this the push fails with a 503, which Case Management's push pipeline retries.
    private const int MaxAttempts = 3;

    public static void UseChangeRequestStatusEndpoints(this IEndpointRouteBuilder app) =>
        app.MapPost(Route, StatusChangedFromCaseManagement)
            .RequireAuthorization(policy =>
                policy
                    .AddAuthenticationSchemes(CaseManagementAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
            );

    /// <summary>
    /// A Case Management state id to the item status it projects onto, as for an application, minus the statuses an
    /// item has no equivalent for. Anything else (awaiting-decision, the pending-contact states, anything added
    /// later) leaves the status alone and only advances the item's ordering watermark. "assessment-started" is
    /// never pushed (its start step is excluded) and is here, as on the application route, only as a safeguard.
    /// </summary>
    internal static ChangeItemStatus? MapState(string toStateId) =>
        toStateId switch
        {
            "submitted" => ChangeItemStatus.Submitted,
            "duly-made" or "assessment-started" => ChangeItemStatus.DulyMade,
            "updated" or "assessment-in-progress" => ChangeItemStatus.Updated,
            // RA-495 task 007 replaces this with the grant: the site joins the accreditation in the same write.
            "approved" => ChangeItemStatus.Approved,
            "rejected" => ChangeItemStatus.Rejected,
            _ => null,
        };

    private static bool IsTerminal(ChangeItemStatus status) =>
        status is ChangeItemStatus.Approved or ChangeItemStatus.Rejected or ChangeItemStatus.Withdrawn;

    private static bool IsUnderAssessment(ChangeItemStatus status) =>
        status is ChangeItemStatus.Submitted or ChangeItemStatus.DulyMade or ChangeItemStatus.Updated;

    private sealed record Push(
        Guid WorkItemId,
        StatusChangedFromCaseManagementRequest Request,
        ChangeItemStatus? MappedStatus,
        string CorrelationId
    );

    private static async Task<IResult> StatusChangedFromCaseManagement(
        Guid workItemId,
        StatusChangedFromCaseManagementRequest request,
        IChangeRequestPersistence persistence,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var logger = loggerFactory.CreateLogger(typeof(ChangeRequestStatusEndpoints));
        // Sent by ManagementBe's push hook for tracing only; its absence never fails the request.
        var correlationId = httpContext.Request.Headers.TryGetValue("X-Correlation-Id", out var values)
            ? values.ToString()
            : "(absent)";
        var push = new Push(workItemId, request, MapState(request.ToStateId), correlationId);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var changeRequest = await persistence.GetByCaseManagementWorkItemIdAsync(workItemId, cancellationToken);
            if (changeRequest is null)
            {
                logger.LogWarning(
                    "Change request status push: no item for workItemId={WorkItemId} correlationId={CorrelationId}",
                    workItemId,
                    correlationId
                );
                return Results.NotFound();
            }
            var item = changeRequest.Sites.Single(s => s.CaseManagementWorkItemId == workItemId);

            if (GuardPush(logger, item, push) is { } guarded)
                return guarded;

            if (push.MappedStatus is { } status)
                item.Status = status;
            item.CaseManagementStatusUpdatedAt = request.OccurredAt;

            if (await persistence.UpdateAsync(changeRequest, cancellationToken))
            {
                LogApplied(logger, item, push);
                return Results.Ok(item);
            }
        }

        logger.LogWarning(
            "Change request status push: lost the version check {Attempts} times for workItemId={WorkItemId} correlationId={CorrelationId}",
            MaxAttempts,
            workItemId,
            correlationId
        );
        return Results.Problem(
            detail: "The change request was being changed at the same time. Try again.",
            statusCode: StatusCodes.Status503ServiceUnavailable
        );
    }

    // The application route's guards, in its order: ordering, final status, approve/reject legality. Null when the
    // push may be applied.
    private static IResult? GuardPush(ILogger logger, ChangeRequestSite item, Push push)
    {
        var (workItemId, request, mapped, correlationId) = push;

        // Ordering, not status precedence: a push not strictly after the last one applied is a duplicate or an
        // out-of-order retry, accepted but not applied.
        if (item.CaseManagementStatusUpdatedAt is { } lastUpdated && request.OccurredAt <= lastUpdated)
        {
            LogIgnored(logger, "out-of-order or duplicate push", item, push);
            return Results.Ok(item);
        }

        // A granted, refused or withdrawn item never moves again. A repeat of the final status it already has is
        // Case Management's confirm-contact retry, accepted with no write. Unmapped pushes change no status.
        if (mapped is not null && IsTerminal(item.Status))
        {
            if (mapped == item.Status)
            {
                LogIgnored(logger, "idempotent repeat of the final status", item, push);
                return Results.Ok(item);
            }
            logger.LogWarning(
                "Change request status push: item status {Status} is final and cannot take toStateId={ToStateId} for workItemId={WorkItemId} correlationId={CorrelationId}",
                item.Status,
                request.ToStateId,
                workItemId,
                correlationId
            );
            return Results.Conflict(
                "The change request item is already Approved, Rejected or Withdrawn and can no longer be updated."
            );
        }

        if ((mapped is ChangeItemStatus.Approved or ChangeItemStatus.Rejected) && !IsUnderAssessment(item.Status))
        {
            logger.LogWarning(
                "Change request status push: item status {Status} is not valid for toStateId={ToStateId} for workItemId={WorkItemId} correlationId={CorrelationId}",
                item.Status,
                request.ToStateId,
                workItemId,
                correlationId
            );
            return Results.Conflict(
                "The change request item must be 'Submitted', 'DulyMade' or 'Updated' to approve or reject."
            );
        }

        return null;
    }

    private static void LogIgnored(ILogger logger, string reason, ChangeRequestSite item, Push push)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Change request status push: {Reason} ignored for workItemId={WorkItemId} toStateId={ToStateId} occurredAt={OccurredAt} status={Status} lastUpdated={LastUpdated} correlationId={CorrelationId}",
                reason,
                push.WorkItemId,
                push.Request.ToStateId,
                push.Request.OccurredAt,
                item.Status,
                item.CaseManagementStatusUpdatedAt,
                push.CorrelationId
            );
        }
    }

    private static void LogApplied(ILogger logger, ChangeRequestSite item, Push push)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Change request status push applied: item {ItemId} is {Status} for workItemId={WorkItemId} toStateId={ToStateId} correlationId={CorrelationId}",
                item.ItemId,
                item.Status,
                push.WorkItemId,
                push.Request.ToStateId,
                push.CorrelationId
            );
        }
    }
}
