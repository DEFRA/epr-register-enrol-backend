using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationChanges;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public interface ICaseWorkingApiAdapter
{
    Task<CaseWorkingSubmissionResult> SubmitApplicationAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    );

    // RA-492: creates an add-overseas-site work item for a submitted in-year change request and
    // returns ManagementBe's in-year change reference for it (format: see "Application
    // reference" in ManagementBe's docs/work-items.md). Allowed to throw - the caller only marks
    // the request Submitted once this succeeds.
    Task<CaseWorkingSubmissionResult> SubmitChangeRequestAsync(
        AccreditationChangeRequest changeRequest,
        AccreditationApplicationModel accreditation,
        CancellationToken cancellationToken = default
    );

    // NotificationStatus is null | "sent" | "failed", derived from the linked work item's audit
    // log; SlaDueDate (RA-415) comes straight off the same work item response — the Case
    // Management service's SLA due date is the Registration & Accreditation service's single
    // source of truth for "Due date", so no separate lookup is needed.
    // Must never throw: a missing link, an unreachable ManagementBe, or a malformed response
    // all degrade to an all-null result rather than failing the caller's GetById response
    // (RA102-j7s).
    Task<NotificationStatusResult> GetNotificationStatusAsync(
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

    // Courtesy notification fired after a new ORS or interim site is saved (RA-294 AC05 /
    // RA-297 AC04) — not part of the save transaction, so like GetNotificationStatusAsync this
    // must never throw. siteNumber is only populated when siteType == "interim".
    Task NotifySiteAddedAsync(
        AccreditationApplicationModel application,
        string siteType,
        string orsId,
        string? siteNumber,
        bool isNewSite,
        CancellationToken cancellationToken = default
    );

    // RA-766 (temporary; removed with ChargeCorrectionMigration): reads the state, charge and
    // duly-made marker of a work item (ManagementBe's GET /work-items/{id}). Allowed to throw
    // (an HttpRequestException carrying the status code for a non-success response): the
    // migration counts a failure and carries on.
    Task<CaseWorkingWorkItem> GetWorkItemAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default
    );

    // RA-766 (temporary; removed with ChargeCorrectionMigration): sets the charge held on a work
    // item (ManagementBe's PUT /work-items/re-accreditation/{id}/charge). A 409 (the work item
    // is closed) is Closed; any other non-success response throws.
    Task<SetChargeOutcome> SetChargeAsync(
        Guid workItemId,
        int chargeAmountPence,
        CancellationToken cancellationToken = default
    );

    // Calls ManagementBe's withdraw action (RA-252) so the linked work item — and its
    // automatic audit trail — reflect the withdrawal. Like ResumeFromQueryAsync this is
    // allowed to throw: the caller (Withdraw) must know whether the call succeeded before
    // persisting the Withdrawn transition. contactDetails identifies the user actually
    // withdrawing now, not the application's original submitter.
    Task<WithdrawResult> WithdrawApplicationAsync(
        AccreditationApplicationModel application,
        QuerySubmitterContactDetails contactDetails,
        string reason,
        CancellationToken cancellationToken = default
    );
}

// CaseReference is ManagementBe's case reference: an application's AP reference, or a change
// request's in-year change reference (formats: see "Application reference" in ManagementBe's
// docs/work-items.md). WorkItemId is null when ManagementBe's response didn't carry a usable id
// (e.g. unparseable body) — that must not fail the submission, since the reference is already
// known.
public sealed record CaseWorkingSubmissionResult(string CaseReference, Guid? WorkItemId);

public sealed record NotificationStatusResult(string? NotificationStatus, DateTime? SlaDueDate);

public sealed record ResumeFromQueryResult(bool IsSuccess);

// RA-766 (temporary): the parts of a ManagementBe work item the charge correction needs.
// ChargeAmountPence is null when none is stored; DulyMade is whether a payment date has been
// recorded (what ManagementBe's own charge-corrected audit entry reports).
public sealed record CaseWorkingWorkItem(string? StateId, int? ChargeAmountPence, bool DulyMade);

// RA-766 (temporary): Closed means ManagementBe answered 409: the work item is approved,
// refused, withdrawn or awaiting applicant contact, and its charge is never changed.
public enum SetChargeOutcome
{
    Updated,
    Unchanged,
    Closed,
}

// Refused means ManagementBe answered 409: the application has already been decided.
// Failed is every other problem, which the caller still reports as a service fault.
public enum WithdrawResult
{
    Succeeded,
    Refused,
    Failed,
}
