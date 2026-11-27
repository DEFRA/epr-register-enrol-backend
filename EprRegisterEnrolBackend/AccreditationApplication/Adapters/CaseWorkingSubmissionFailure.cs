using System.Net;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

/// <summary>
/// The one place a failed submission to ManagementBe is classified: whether it is Case Management's own failure (as
/// opposed to a defect), and whether ManagementBe may still have saved it. The adapter and the change request submit
/// both ask here, so they can't disagree.
/// </summary>
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
    /// Whether ManagementBe may hold the submission that failed with <paramref name="ex"/>. Only a failure raised while
    /// sending or after can follow a save: any other 4xx, a timeout, a 5xx, an unreadable 2xx or the caller going
    /// away mid-send. Not configured means nothing was sent, the statuses above mean ManagementBe rejected the
    /// submission before saving it, and any other exception is a defect raised before anything was sent.
    /// </summary>
    public static bool MayHold(Exception ex) =>
        ex switch
        {
            CaseWorkingNotConfiguredException => false,
            HttpRequestException { StatusCode: { } status } => !s_holdsNothingStatuses.Contains(status),
            HttpRequestException or CaseWorkingApiTimeoutException or OperationCanceledException => true,
            _ => false,
        };

    /// <summary>
    /// Whether <paramref name="ex"/> is Case Management's own failure, answered as a 503, rather than a defect or the
    /// caller going away, which are rethrown. A partly held submission is judged by the failure that stopped it.
    /// </summary>
    public static bool IsCaseManagementFailure(Exception ex) =>
        (ex is ChangeRequestPartlySubmittedException { InnerException: { } inner } ? inner : ex)
            is HttpRequestException
                or CaseWorkingApiTimeoutException
                or CaseWorkingNotConfiguredException;
}
