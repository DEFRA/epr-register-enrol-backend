using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public interface ICaseWorkingApiAdapter
{
    Task<CaseWorkingSubmissionResult> SubmitApplicationAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    );
}

// WorkItemId is null when ManagementBe's response didn't carry a usable id (e.g. unparseable
// body) — that must not fail the submission, since ApplicationReference is already known locally.
public sealed record CaseWorkingSubmissionResult(string ApplicationReference, Guid? WorkItemId);
