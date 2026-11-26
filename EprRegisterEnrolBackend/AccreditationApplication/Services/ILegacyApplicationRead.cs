using MongoDB.Bson;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// RA-750: reads one application from the Legacy <c>accreditationApplications</c> collection,
/// for the admin API, exactly as it is stored; RA-751: and the statuses of the applications linked
/// to a page of Case Management work items. Always the Legacy collection, like
/// <see cref="ILegacyApplicationSearch"/>, until RA-677 retires or repoints it.
/// </summary>
public interface ILegacyApplicationRead
{
    /// <summary>
    /// The stored document for <paramref name="reference"/>, or null when there is none. The
    /// reference is the application reference (<c>AP…</c>), or, for an application that has not
    /// been submitted and so has none, its <c>_id</c>. The document is read raw, not through the
    /// model, so fields the model doesn't know and enums stored as numbers come back as they are.
    /// Read-only.
    /// </summary>
    Task<BsonDocument?> GetLegacyApplicationAsync(
        string reference,
        TimeSpan? maxTime = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// RA-751: the application linked to each of <paramref name="workItemIds"/> (by its stored
    /// <c>caseManagementWorkItemId</c>, unique and indexed) and its status, in one query. A work
    /// item with no linked application has no entry. Reads only the id, the link and the status.
    /// Throws <see cref="LegacyApplicationStatusUnreadableException"/> rather than guess a status.
    /// Read-only.
    /// </summary>
    Task<IReadOnlyList<LegacyApplicationStatus>> GetLegacyApplicationStatusesAsync(
        IReadOnlyCollection<Guid> workItemIds,
        TimeSpan? maxTime = null,
        CancellationToken cancellationToken = default
    );
}
