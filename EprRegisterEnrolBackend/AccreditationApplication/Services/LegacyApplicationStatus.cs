using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// RA-751: the Legacy application linked to a Case Management work item (by its stored
/// <see cref="AccreditationApplicationModel.CaseManagementWorkItemId"/>), and its status.
/// </summary>
public sealed record LegacyApplicationStatus(
    Guid WorkItemId,
    ObjectId ApplicationId,
    ApplicationStatus ApplicationStatus
);

/// <summary>
/// RA-751: a Legacy application linked to a requested work item can't be read as a status: it has
/// none stored, or one that isn't an <see cref="ApplicationStatus"/>, or another application is
/// linked to the same work item. Never answered with a made-up status.
/// </summary>
public sealed class LegacyApplicationStatusUnreadableException(
    Guid workItemId,
    ObjectId applicationId,
    string reason,
    Exception? inner = null
) : Exception($"Legacy application {applicationId} for work item {workItemId}: {reason}", inner)
{
    public Guid WorkItemId { get; } = workItemId;
    public ObjectId ApplicationId { get; } = applicationId;
    public string Reason { get; } = reason;
}
