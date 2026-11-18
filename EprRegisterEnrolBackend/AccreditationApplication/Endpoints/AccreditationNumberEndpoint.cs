using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using MongoDB.Bson;
using MongoDB.Driver;
using static EprRegisterEnrolBackend.AccreditationApplication.Endpoints.AccreditationApplicationEndpoints;

namespace EprRegisterEnrolBackend.AccreditationApplication.Endpoints;

/// <summary>
/// RA-783: <c>POST {organisationId}/{applicationId}/accreditation-number</c>, step 1 of a
/// case management approval. Mapped in <see cref="AccreditationApplicationEndpoints"/>.
/// </summary>
internal static class AccreditationNumberEndpoint
{
    // RA-783: the accreditation reference is built by AccreditationNumberRules from the
    // operator's history: a continuous renewal keeps last year's number with the year changed; a
    // first-time accreditation or a renewal after a break gets the registration's agency, process
    // type and OrgId with a new random sequence. Glass is always GR or GO. AccreditationNumberIssuer
    // gathers the history and checks holders. It is never drawn from a sequence counter.
    //
    // Deliberately takes no request body. The nation, OrgId, year and regenerate an older
    // management-be still sends are ignored: the application's stored Year is authoritative, and
    // an issued reference is never regenerated. So this backend accepts an older management-be,
    // but an older backend rejects the new body-less request (400): deploy this backend first.
    internal static async Task<IResult> GenerateOrUpdateAccreditationNumber(
        string organisationId,
        string applicationId,
        IAccreditationApplicationPersistence persistence,
        IDualWriteUnitOfWork unitOfWork,
        AccreditationNumberIssuer issuer,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var call = new Call(
            organisationId,
            applicationId,
            persistence,
            unitOfWork,
            issuer,
            loggerFactory,
            cancellationToken
        );

        // RA-783 AC04: the holder check runs before the save, so two approvals at the same moment
        // can both save one new number. Each checks again once its save has committed, when the
        // other's save is visible to at least one of them, and draws again on a clash. A
        // continuous renewal's number is fixed, so it isn't drawn again.
        string? clashing = null;
        for (var round = 1; round <= AccreditationNumberIssuer.MaxAttempts; round++)
        {
            var (result, attempt) = await SaveAsync(call, replacing: clashing);
            if (
                attempt?.Issued is not { } issued
                || issued.Case == AccreditationNumberCase.ContinuousRenewal
            )
            {
                attempt?.LogIssued?.Invoke();
                return result;
            }

            call.Logger.LogInformation(
                SavedTemplate,
                issued.AccreditationNumber,
                applicationId,
                round
            );
            var clashes = await persistence.GetOtherAccreditationReferenceHoldersAsync(
                issued.AccreditationNumber,
                attempt.SavedId!.Value
            );
            if (clashes.Count == 0)
            {
                attempt.LogIssued?.Invoke();
                return result;
            }

            clashing = issued.AccreditationNumber;
            call.Logger.LogWarning(
                ClashTemplate,
                clashing,
                string.Join(",", clashes.Select(c => c.Id)),
                applicationId,
                round
            );
        }

        return await GiveUpAsync(call, clashing!);
    }

    private const string SavedTemplate =
        "GenerateOrUpdateAccreditationNumber: generated and saved accreditation number "
        + "{AccreditationNumber}; checking no other application saved it at the same moment. "
        + "applicationId={ApplicationId} round={Round}";
    private const string ClashTemplate =
        "GenerateOrUpdateAccreditationNumber: accreditation number {AccreditationNumber} was "
        + "written, and the check for duplicates found it also held by {OtherApplicationIds}, so "
        + "it is drawn again. applicationId={ApplicationId} round={Round}";
    private const string GaveUpTemplate =
        "GenerateOrUpdateAccreditationNumber: every accreditation number generated in {Attempts} "
        + "attempts was also saved by another application; clearing {AccreditationNumber} so "
        + "trying again draws a new one. applicationId={ApplicationId}";

    // What one call to the endpoint works with, so each step takes one argument.
    private sealed record Call(
        string OrganisationId,
        string ApplicationId,
        IAccreditationApplicationPersistence Persistence,
        IDualWriteUnitOfWork UnitOfWork,
        AccreditationNumberIssuer Issuer,
        ILoggerFactory LoggerFactory,
        CancellationToken CancellationToken
    )
    {
        public ILogger Logger { get; } =
            LoggerFactory.CreateLogger("AccreditationApplicationEndpoints");
    }

    // Issues and saves a number in one transaction. The attempt is null unless it committed.
    private static async Task<(IResult Result, IssueAttempt? Attempt)> SaveAsync(
        Call call,
        string? replacing
    )
    {
        // The work can re-run, so only the attempt that committed is kept.
        IssueAttempt? attempt = null;
        var result = await call.UnitOfWork.RunAsync(
            nameof(GenerateOrUpdateAccreditationNumber),
            call.ApplicationId,
            async (session, _) =>
            {
                attempt = await IssueAccreditationNumberAsync(
                    call.Persistence,
                    call.Issuer,
                    call.LoggerFactory,
                    call.OrganisationId,
                    call.ApplicationId,
                    replacing,
                    session
                );
                return attempt.Result;
            },
            call.CancellationToken
        );
        return (result, Committed(result) ? attempt : null);
    }

    // Every number drawn was taken at the same moment. Clears the last one, so trying again draws
    // a new number rather than returning the one another application holds.
    private static async Task<IResult> GiveUpAsync(Call call, string clashing)
    {
        call.Logger.LogError(
            GaveUpTemplate,
            AccreditationNumberIssuer.MaxAttempts,
            clashing,
            call.ApplicationId
        );
        var cleared = await call.UnitOfWork.RunAsync(
            nameof(GenerateOrUpdateAccreditationNumber),
            call.ApplicationId,
            (session, _) =>
                ClearAccreditationNumberAsync(
                    call.Persistence,
                    call.OrganisationId,
                    call.ApplicationId,
                    clashing,
                    session
                ),
            call.CancellationToken
        );
        return Committed(cleared)
            ? Results.Conflict(
                "Another application was issued the same accreditation number at the same moment "
                    + $"in each of {AccreditationNumberIssuer.MaxAttempts} attempts. Try again."
            )
            : cleared;
    }

    private static async Task<IResult> ClearAccreditationNumberAsync(
        IAccreditationApplicationPersistence persistence,
        string organisationId,
        string applicationId,
        string accreditationNumber,
        IClientSessionHandle? session
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId, session);
        if (application?.AccreditationReference != accreditationNumber)
            return Results.NoContent();
        application.AccreditationReference = null;
        application.DateLastEdited = DateTime.UtcNow;
        return await persistence.UpdateAsync(application, session) is null
            ? Results.Problem("Failed to clear the accreditation number.")
            : Results.NoContent();
    }

    private sealed record IssueAttempt(
        IResult Result,
        IssuedAccreditationNumber? Issued = null,
        ObjectId? SavedId = null,
        Action? LogIssued = null
    );

    /// <param name="replacing">A number this application saved that another application saved
    /// at the same moment, to draw again rather than return.</param>
    private static async Task<IssueAttempt> IssueAccreditationNumberAsync(
        IAccreditationApplicationPersistence persistence,
        AccreditationNumberIssuer issuer,
        ILoggerFactory loggerFactory,
        string organisationId,
        string applicationId,
        string? replacing,
        IClientSessionHandle? session
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId, session);
        if (application is null)
            return new IssueAttempt(Results.NotFound());
        if (RejectIfTerminal(application) is { } conflict)
            return new IssueAttempt(conflict);

        // RA-448 AC9: registration precedes and is independent of accreditation - an
        // accreditation number is never issued with nothing to accredit against.
        if (application.RegistrationReference is not { } registrationReference)
            return new IssueAttempt(
                Results.Conflict(
                    "Application has no registration number yet; an accreditation number cannot be issued."
                )
            );

        // Idempotent retry: an issued reference may already be quoted externally, so it is
        // returned unchanged and never re-derived. This holds while the application is open
        // (between approval steps 1 and 2); once Approved, RejectIfTerminal above returns 409.
        // The exception is a number another application saved at the same moment, drawn again.
        if (
            application.AccreditationReference is { } held
            && held != replacing
        )
            return new IssueAttempt(Results.Ok(application));

        var outcome = await issuer.IssueAsync(application, registrationReference);
        if (outcome.Issued is not { } issued)
            return new IssueAttempt(
                outcome.Failure == AccreditationNumberIssueFailure.InvalidHistory
                    ? Results.UnprocessableEntity(outcome.FailureMessage)
                    : Results.Conflict(outcome.FailureMessage)
            );

        application.AccreditationReference = issued.AccreditationNumber;
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await persistence.UpdateAsync(application, session);
        if (updated is null)
            return new IssueAttempt(Results.Problem("Failed to update accreditation number."));

        return new IssueAttempt(
            Results.Ok(updated),
            issued,
            updated.Id,
            () =>
            {
                var logger = loggerFactory.CreateLogger("AccreditationApplicationEndpoints");
                LogAccreditationReferenceIssued(logger, updated, issued);
                if (issued.GlassProcessUnknown)
                    logger.LogError(
                        GlassProcessUnknownTemplate,
                        updated.ApplicationReference,
                        updated.Id,
                        updated.OrganisationId,
                        updated.OrgId,
                        updated.RegistrationReference,
                        updated.AccreditationReference
                    );
            }
        );
    }

    private const string GlassProcessUnknownTemplate =
        "GenerateOrUpdateAccreditationNumber: no glass recycling process held or carried by an "
        + "earlier number, so the accreditation number was issued as other (GO); confirm the "
        + "process. applicationReference={ApplicationReference} applicationId={ApplicationId} "
        + "organisationId={OrganisationId} orgId={OrgId} "
        + "registrationReference={RegistrationReference} "
        + "accreditationReference={AccreditationReference}";

    // RA-690: the source of each issued reference is logged, not stored, so support can explain
    // any reference without a schema change. A previous-accreditation source also names the
    // application it came from, and any withdrawn applications already holding the reference are
    // named so support can see why two records share it.
    private const string IssuedTemplate =
        "GenerateOrUpdateAccreditationNumber: issued accreditationReference={AccreditationReference} for applicationId={ApplicationId} as case={NumberCase} from sourceKind={SourceKind} sourceReference={SourceReference}";
    private const string SourceApplicationTemplate = " sourceApplicationId={SourceApplicationId}";
    private const string WithdrawnHoldersTemplate = " withdrawnHolderIds={WithdrawnHolderIds}";

    private static void LogAccreditationReferenceIssued(
        ILogger logger,
        AccreditationApplicationModel application,
        IssuedAccreditationNumber issued
    )
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;

        var (reference, id) = (application.AccreditationReference, application.Id);
        var withdrawnIds = string.Join(",", issued.WithdrawnHolderIds);
        switch (issued.SourceApplicationId, issued.WithdrawnHolderIds.Count)
        {
            case (null, 0):
                logger.LogInformation(
                    IssuedTemplate,
                    reference,
                    id,
                    issued.Case,
                    issued.SourceKind,
                    issued.SourceReference
                );
                break;
            case (null, _):
                logger.LogInformation(
                    IssuedTemplate + WithdrawnHoldersTemplate,
                    reference,
                    id,
                    issued.Case,
                    issued.SourceKind,
                    issued.SourceReference,
                    withdrawnIds
                );
                break;
            case (_, 0):
                logger.LogInformation(
                    IssuedTemplate + SourceApplicationTemplate,
                    reference,
                    id,
                    issued.Case,
                    issued.SourceKind,
                    issued.SourceReference,
                    issued.SourceApplicationId
                );
                break;
            default:
                logger.LogInformation(
                    IssuedTemplate + SourceApplicationTemplate + WithdrawnHoldersTemplate,
                    reference,
                    id,
                    issued.Case,
                    issued.SourceKind,
                    issued.SourceReference,
                    issued.SourceApplicationId,
                    withdrawnIds
                );
                break;
        }
    }
}
