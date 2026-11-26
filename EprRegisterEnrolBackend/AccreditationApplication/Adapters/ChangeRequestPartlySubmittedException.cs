using System.Net;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

/// <summary>
/// RA-682: a change request holding adds and updates is sent to ManagementBe as one submission per change type. This
/// is thrown when an earlier group was accepted and a later one failed: ManagementBe holds
/// <see cref="HeldChangeTypes"/>, and <see cref="RefusedMayBeHeld"/> says whether it may also hold the group that
/// failed (a timeout or a 5xx), or certainly doesn't (a 4xx it returns before saving anything).
/// </summary>
public sealed class ChangeRequestPartlySubmittedException(
    IReadOnlyCollection<string> heldChangeTypes,
    string refusedChangeType,
    bool refusedMayBeHeld,
    Exception innerException
)
    : HttpRequestException(
        $"ManagementBe holds the {string.Join(", ", heldChangeTypes)} sites of a change request but did not accept its {refusedChangeType} sites.",
        innerException
    )
{
    public IReadOnlyCollection<string> HeldChangeTypes { get; } = heldChangeTypes;
    public string RefusedChangeType { get; } = refusedChangeType;
    public bool RefusedMayBeHeld { get; } = refusedMayBeHeld;
}

/// <summary>Whether a failed submission to ManagementBe may still have been saved by it.</summary>
public static class CaseWorkingSubmissionFailure
{
    // The statuses ManagementBe returns from validating a submission, before it saves anything, so
    // they are proof it holds nothing. Deliberately not every 4xx: a 408 or 429, or any 4xx from a
    // gateway or proxy in front of ManagementBe, says nothing about whether the submission was saved.
    private static readonly HttpStatusCode[] s_holdsNothingStatuses =
    [
        HttpStatusCode.BadRequest,
        HttpStatusCode.Unauthorized,
        HttpStatusCode.Forbidden,
        HttpStatusCode.NotFound,
        HttpStatusCode.Conflict,
    ];

    /// <summary>
    /// Not configured means nothing was sent, and the statuses above mean ManagementBe rejected the submission before
    /// saving it. Any other 4xx, a timeout, a 5xx or an unreadable 2xx may follow a save.
    /// </summary>
    public static bool MayHold(Exception ex) =>
        ex switch
        {
            CaseWorkingNotConfiguredException => false,
            HttpRequestException { StatusCode: { } status } => !s_holdsNothingStatuses.Contains(status),
            _ => true,
        };
}
