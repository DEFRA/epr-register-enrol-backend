using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationChanges;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public interface ICaseWorkingApiAdapter
{
    Task<CaseWorkingSubmissionResult> SubmitApplicationAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    );

    // RA-492, RA-739: sends a submitted add-overseas-site change request to ManagementBe, which
    // creates one work item per site. Returns the submission reference they share and each site's
    // own work item id and in-year change reference (format: see "Application reference" in
    // ManagementBe's docs/work-items.md). Throws HttpRequestException unless every submitted site
    // came back with a reference; the caller reopens the request on any failure.
    Task<ChangeRequestSubmissionResult> SubmitChangeRequestAsync(
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

    // RA-766, RA-768 (temporary; removed with the last of ChargeCorrectionMigration and
    // OverseasSitesCorrectionMigration): reads the parts of a work item those one-off corrections
    // need (ManagementBe's GET /work-items/{id}): its state, charge, duly-made marker, the ids of
    // the overseas sites it holds and its query-responded audit entries. Not a general work item read. The caller names the correction it
    // acts as, which ManagementBe records as the end user. Allowed to throw (an
    // HttpRequestException carrying the status code for a non-success response): each migration
    // counts a failure and carries on.
    Task<CaseWorkingWorkItem> GetWorkItemAsync(
        Guid workItemId,
        CaseWorkingCorrectionActor actor,
        CancellationToken cancellationToken = default
    );

    // RA-766 (temporary; removed with ChargeCorrectionMigration): sets the charge held on a work
    // item (ManagementBe's PUT /work-items/re-accreditation/{id}/charge). A 409 with errorCode
    // work-item-closed is Closed; any other non-success response throws, including a 409 for a
    // work item that stayed busy (errorCode concurrency-conflict).
    Task<SetChargeOutcome> SetChargeAsync(
        Guid workItemId,
        int chargeAmountPence,
        CancellationToken cancellationToken = default
    );

    // RA-768 (temporary; removed with OverseasSitesCorrectionMigration): removes the named sites
    // from the work item's payload.overseasSites.sites (ManagementBe's POST
    // /work-items/re-accreditation/{id}/overseas-sites/remove), acting as the overseas sites
    // correction. A 409 with reason decided or duly-made is that outcome; any other non-success
    // response throws an HttpRequestException carrying the status, including a 409 with reason
    // concurrency-conflict (the work item stayed busy) or any other reason. A 200 whose body has no
    // removed list throws a JsonException.
    Task<RemoveOverseasSitesResult> RemoveOverseasSitesAsync(
        Guid workItemId,
        IReadOnlyList<CaseWorkingSiteIds> sites,
        string reason,
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

// CaseReference is ManagementBe's case reference for an application: its AP reference (format:
// see "Application reference" in ManagementBe's docs/work-items.md). WorkItemId is null when
// ManagementBe's response didn't carry a usable id (e.g. unparseable body) — that must not fail
// the submission, since the reference is already known.
public sealed record CaseWorkingSubmissionResult(string CaseReference, Guid? WorkItemId);

// RA-739: ManagementBe's answer to a change request: the submission reference shared by its work
// items, and one entry per submitted site, in the request's site order.
public sealed record ChangeRequestSubmissionResult(
    string SubmissionReference,
    IReadOnlyList<SiteWorkItemResult> Sites
);

// WorkItemId is null when ManagementBe's entry carried no usable id; the reference is what counts.
public sealed record SiteWorkItemResult(int SiteId, string CaseReference, Guid? WorkItemId);

public sealed record NotificationStatusResult(string? NotificationStatus, DateTime? SlaDueDate);

public sealed record ResumeFromQueryResult(bool IsSuccess);

// RA-766, RA-768 (temporary): the parts of a ManagementBe work item the one-off corrections need.
// ChargeAmountPence is null when none is stored; DulyMade is whether a payment date has been
// recorded (ManagementBe's own duly-made rule: payload.paymentDate present and not null).
// OverseasSites is null when payload.overseasSites.sites is missing or is not a readable list of
// sites; a site with neither id is kept, with both ids null, so the caller can refuse it.
// QueryResponses holds the work item's query-responded audit entries (RA-768); it is null when the
// audit log is missing or any entry is unreadable, so a caller can never read it as "none".
public sealed record CaseWorkingWorkItem(
    string? StateId,
    int? ChargeAmountPence,
    bool DulyMade,
    IReadOnlyList<CaseWorkingSiteIds>? OverseasSites,
    IReadOnlyList<CaseWorkingQueryResponse>? QueryResponses
)
{
    // The states ManagementBe never changes a charge or the overseas sites in: its terminal states
    // plus the two pending-contact states, which are already a decided outcome (this service shows
    // them as AwaitingDecision, so the application status alone would not catch them).
    //
    // A copy: the source of truth is ReAccreditationCorrectionGuards.IsDecided in
    // epr-register-enrol-management-be (TerminalStates plus
    // StandardConfirmContactGuard.ForPendingState), which both its PUT .../charge and its
    // POST .../overseas-sites/remove call. The dry runs need
    // it here because a dry run never calls either. If the list changes there before both
    // migrations are removed, change it here too.
    internal static readonly IReadOnlySet<string> DecidedStates = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "approved",
        "rejected",
        "withdrawn",
        "approved-pending-contact",
        "rejected-pending-contact",
    };

    public bool IsDecided => StateId is { } stateId && DecidedStates.Contains(stateId);
}

// RA-768 (temporary): one query-responded audit entry: when the operator's answer reached
// ManagementBe, and the section keys it answered (stored comma-joined in details.sectionKeys).
public sealed record CaseWorkingQueryResponse(
    DateTime CreatedAt,
    IReadOnlyList<string> SectionKeys
);

// RA-768 (temporary): a site's ids as ManagementBe holds them. SiteId is the string form of this
// service's integer site id (ManagementBe matches on the string form).
public sealed record CaseWorkingSiteIds(string? OrsId, string? SiteId);

// RA-766, RA-768 (temporary): the identity a one-off correction acts as. ManagementBe requires an
// end-user id on every call and records it on the audit entries it writes.
public sealed record CaseWorkingCorrectionActor(string UserId, string UserName)
{
    public static readonly CaseWorkingCorrectionActor ChargeCorrection = new(
        "ra-766-charge-correction",
        "RA-766 charge correction"
    );

    public static readonly CaseWorkingCorrectionActor OverseasSitesCorrection = new(
        "ra-768-overseas-sites-correction",
        "RA-768 overseas sites correction"
    );
}

// RA-768 (temporary): Updated when ManagementBe removed at least one site (Removed lists the ids of
// the sites it removed, as it held them); Unchanged when every named site was already gone;
// DulyMade and Decided when it refused with a 409 and wrote nothing.
public enum RemoveOverseasSitesOutcome
{
    Updated,
    Unchanged,
    DulyMade,
    Decided,
}

public sealed record RemoveOverseasSitesResult(
    RemoveOverseasSitesOutcome Outcome,
    IReadOnlyList<CaseWorkingSiteIds> Removed
);

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
