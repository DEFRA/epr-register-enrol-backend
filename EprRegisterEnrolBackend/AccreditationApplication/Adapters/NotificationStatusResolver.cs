namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

// Wire shape for the subset of ManagementBe's GET /work-items/{id} response this adapter
// needs — just enough of the audit log to derive a notification status, not the full contract.
internal sealed class WorkItemDetailResponseDto
{
    public List<WorkItemAuditEntryDto>? AuditLog { get; init; }
}

internal sealed class WorkItemAuditEntryDto
{
    public string? Action { get; init; }
    public Dictionary<string, string?>? Details { get; init; }
    public DateTime CreatedAt { get; init; }
}

// RA102-j7s: derives an operator-facing notification status from a work item's audit log.
// Mirrors epr-register-enrol-management-fe's notificationFailureDetected (RA-211) — a
// notification-failed entry is "unresolved" unless a LATER notification-sent entry for the
// SAME template exists, so an unrelated notification succeeding (e.g. DulyMade) can never mask
// a different one's still-unresolved failure (e.g. Queried). Entries missing a templateKey
// (older data) fall back to treating any later success as resolving it.
internal static class NotificationStatusResolver
{
    public static string? Resolve(IReadOnlyCollection<WorkItemAuditEntryDto>? auditLog)
    {
        if (auditLog is null || auditLog.Count == 0)
        {
            return null;
        }

        var failed = auditLog.Where(e => e.Action == "notification-failed").ToList();
        var sent = auditLog.Where(e => e.Action == "notification-sent").ToList();

        var hasUnresolvedFailure = failed.Any(failure =>
            !sent.Any(success => Resolves(failure, success))
        );

        if (hasUnresolvedFailure)
        {
            return "failed";
        }

        return sent.Count > 0 ? "sent" : null;
    }

    private static bool Resolves(WorkItemAuditEntryDto failure, WorkItemAuditEntryDto success)
    {
        if (success.CreatedAt <= failure.CreatedAt)
        {
            return false;
        }

        var failureTemplate = TemplateKeyOf(failure);
        var successTemplate = TemplateKeyOf(success);
        if (failureTemplate is not null && successTemplate is not null)
        {
            return string.Equals(successTemplate, failureTemplate, StringComparison.Ordinal);
        }

        return true;
    }

    private static string? TemplateKeyOf(WorkItemAuditEntryDto entry) =>
        entry.Details is not null && entry.Details.TryGetValue("templateKey", out var value)
            ? value
            : null;
}
