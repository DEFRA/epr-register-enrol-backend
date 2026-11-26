using System.Net;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

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
