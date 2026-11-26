using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-495: Case Management pushes each add-ORS work item's status changes to the change request item it was raised
/// for. The same caller, auth scheme, body and guards as the accreditation application's case-management status
/// route (<c>AccreditationApplicationEndpoints.StatusChangedFromCaseManagement</c>), applied to one item rather than
/// a whole application. RA-495 task 007: every push runs in the dual-write unit of work, and <c>approved</c> grants the
/// site there (<see cref="ChangeRequestGrant"/>), so the item and the accreditation commit together or not at all.
/// AC6: <c>approved</c> must carry the <c>effectiveDate</c> the caseworker entered, which the site is accredited from;
/// without it the push is a 400 and nothing is read or written.
/// </summary>
public static class ChangeRequestStatusEndpoints
{
    internal const string Route = "api/v1/accreditation-changes/case-management/{workItemId}/status";

    // Sibling items share their request's document, so pushes for them can race on its version. Without dual-write
    // each lost race is read again; past this the push fails with a 503, which Case Management's push pipeline
    // retries. With dual-write the transaction itself retries a write conflict, and anything else rolls back as a 500.
    private const int MaxAttempts = 3;

    private const string Operation = "ChangeRequestStatusChangedFromCaseManagement";

    public static void UseChangeRequestStatusEndpoints(this IEndpointRouteBuilder app) =>
        app.MapPost(Route, StatusChangedFromCaseManagement).RequireCaseManagementCaller();

    /// <summary>
    /// A Case Management state id to the item status it projects onto: the application's mapping
    /// (<see cref="CaseManagementStatusMapping.StatusPushMap"/>, the one table of state ids both push receivers and
    /// the admin API use), projected onto <see cref="ChangeItemStatus"/>. It is not a second table because the state
    /// ids and what they mean are the same; only the target type differs. An item has no AwaitingDecision, so
    /// awaiting-decision, like a state with no entry, leaves the status alone and only advances the item's ordering
    /// watermark. Awaiting-decision is a known state and is not logged; a state with no entry is logged at Error
    /// unless it is one of <see cref="CaseManagementStatusMapping.KnownIgnoredStateIds"/>. A status the table gains
    /// that this projection does not name throws, so adding a state id to the table can't silently skip items
    /// (ChangeRequestStatusEndpointTests pins every entry).
    /// </summary>
    internal static ChangeItemStatus? MapState(string toStateId) =>
        CaseManagementStatusMapping.ForStatusPush(toStateId) switch
        {
            null or ApplicationStatus.AwaitingDecision => null,
            ApplicationStatus.Submitted => ChangeItemStatus.Submitted,
            ApplicationStatus.DulyMade => ChangeItemStatus.DulyMade,
            ApplicationStatus.Updated => ChangeItemStatus.Updated,
            // The grant: the site joins the accreditation in the same transaction (ChangeRequestGrant).
            ApplicationStatus.Approved => ChangeItemStatus.Approved,
            ApplicationStatus.Rejected => ChangeItemStatus.Rejected,
            { } unprojected => throw new InvalidOperationException(
                $"CaseManagementStatusMapping maps toStateId={toStateId} to {unprojected}, which has no change request item status."
            ),
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
        IAccreditationApplicationPersistence applications,
        IDualWriteUnitOfWork unitOfWork,
        HttpContext httpContext,
        CancellationToken cancellationToken
    )
    {
        var logger = httpContext
            .RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ChangeRequestStatusEndpoints));
        // Sent by ManagementBe's push hook for tracing only; its absence never fails the request.
        var correlationId = httpContext.Request.Headers.TryGetValue("X-Correlation-Id", out var values)
            ? values.ToString()
            : "(absent)";
        if (
            CaseManagementStatusMapping.RejectMissingToStateId(logger, Operation, workItemId, request, correlationId)
            is { } missingToStateId
        )
            return missingToStateId;
        // awaiting-decision is in the shared table, so it is known here too, though an item has no status for it.
        CaseManagementStatusMapping.LogUnknownState(logger, Operation, workItemId, request, correlationId);

        var push = new Push(workItemId, request, MapState(request.ToStateId), correlationId);

        // AC6: a grant is accredited from the date the caseworker entered, which only Case Management knows.
        if (push.MappedStatus == ChangeItemStatus.Approved && request.EffectiveDate is null)
        {
            logger.LogWarning(
                "Change request status push: approved without an effectiveDate for workItemId={WorkItemId} correlationId={CorrelationId}",
                workItemId,
                correlationId
            );
            return Results.Problem(
                detail: "An approved push must name the effectiveDate the site is accredited from.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var stores = new PushStores(persistence, applications, httpContext.RequestServices);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Read once outside the transaction for the accreditation application the unit of work migrates and
            // syncs; the work reads it again in the session.
            var known = await persistence.GetByCaseManagementWorkItemIdAsync(workItemId, cancellationToken);
            if (known is null)
            {
                logger.LogWarning(
                    "Change request status push: no item for workItemId={WorkItemId} correlationId={CorrelationId}",
                    workItemId,
                    correlationId
                );
                return Results.NotFound();
            }

            try
            {
                return await unitOfWork.RunAsync(
                    Operation,
                    known.ApplicationId ?? "",
                    (session, token) => ApplyPushAsync(stores, logger, push, session, token),
                    cancellationToken
                );
            }
            catch (ChangeRequestWriteConflictException)
            {
                // Only without dual-write: in a transaction the unit of work rolls it back as a 500.
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

    private sealed record PushStores(
        IChangeRequestPersistence Requests,
        IAccreditationApplicationPersistence Applications,
        IServiceProvider Services
    );

    // One push in the unit of work, every read and write in its session: the guards, the grant when approved, and the
    // item last, so a failure anywhere leaves the item as it was.
    private static async Task<IResult> ApplyPushAsync(
        PushStores stores,
        ILogger logger,
        Push push,
        IClientSessionHandle? session,
        CancellationToken cancellationToken
    )
    {
        var changeRequest = await stores.Requests.GetByCaseManagementWorkItemIdAsync(
            push.WorkItemId,
            cancellationToken,
            session
        );
        if (changeRequest is null)
            return Results.NotFound();
        var item = changeRequest.Sites.Single(s => s.CaseManagementWorkItemId == push.WorkItemId);

        if (GuardPush(logger, item, push) is { } guarded)
            return guarded;

        if (push.MappedStatus == ChangeItemStatus.Approved)
        {
            var accreditedFrom = push.Request.EffectiveDate!.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var refused = await ChangeRequestGrant.ApplyAsync(
                changeRequest,
                item,
                accreditedFrom,
                stores.Applications,
                stores.Services,
                session,
                cancellationToken
            );
            if (refused is not null)
            {
                logger.LogWarning(
                    "Change request status push: nothing to grant for workItemId={WorkItemId} correlationId={CorrelationId}",
                    push.WorkItemId,
                    push.CorrelationId
                );
                return refused;
            }
            item.ValidFrom = accreditedFrom;
        }

        if (push.MappedStatus is { } status)
            item.Status = status;
        item.CaseManagementStatusUpdatedAt = push.Request.OccurredAt;

        if (!await stores.Requests.UpdateAsync(changeRequest, cancellationToken, session))
            throw new ChangeRequestWriteConflictException();

        LogApplied(logger, item, push);
        return Results.Ok(item);
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
