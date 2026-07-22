using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public interface ICaseWorkingApiAdapter
{
    Task<CaseWorkingSubmissionResult> SubmitApplicationAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    );

    // Returns null | "sent" | "failed", derived from the linked work item's audit log.
    // Must never throw: a missing link, an unreachable ManagementBe, or a malformed response
    // all degrade to null rather than failing the caller's GetById response (RA102-j7s).
    Task<string?> GetNotificationStatusAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    );

    // Calls ManagementBe's resume-from-query action (RA-311 MBE-1) with the resubmitted section
    // keys, current values of those sections, and the submitter's contact details. Unlike
    // GetNotificationStatusAsync this is allowed to throw — the caller (Resubmit) must know
    // whether the call succeeded before persisting the Queried -> Updated transition.
    Task<ResumeFromQueryResult> ResumeFromQueryAsync(
        AccreditationApplicationModel application,
        QuerySubmitterContactDetails contactDetails,
        IReadOnlyList<string> sectionKeys,
        CancellationToken cancellationToken = default
    );
}

// WorkItemId is null when ManagementBe's response didn't carry a usable id (e.g. unparseable
// body) — that must not fail the submission, since ApplicationReference is already known locally.
public sealed record CaseWorkingSubmissionResult(string ApplicationReference, Guid? WorkItemId);

public sealed record ResumeFromQueryResult(bool IsSuccess);
