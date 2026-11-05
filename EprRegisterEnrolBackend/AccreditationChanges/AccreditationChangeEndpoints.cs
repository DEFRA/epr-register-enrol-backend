using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-492: in-year change requests against a granted accreditation - adding existing registered
/// overseas reprocessing sites to it. Keyed by the Approved accreditation application, which is
/// only ever read here: the sites stay Registered (pending) until a regulator decision.
/// </summary>
public static class AccreditationChangeEndpoints
{
    internal const string RoutePrefix =
        "api/v1/accreditation-changes/{organisationId}/{applicationId}";

    private const string ConcurrentEditMessage =
        "The change request was changed by someone else. Reload it and try again.";

    public static void UseAccreditationChangeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(RoutePrefix)
            .RequireAuthorization(policy =>
                policy
                    .AddAuthenticationSchemes(FrontendAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
            );

        group.MapGet("", Get);
        group.MapPost("requests/current/sites", AddSite);
        group.MapPatch("requests/current/sites/{siteId:int}", PatchSite);
        group.MapDelete("requests/current/sites/{siteId:int}", RemoveSite);
        group.MapPost("requests/current/bes-evidence/initiate", InitiateBesEvidenceUpload);
        group.MapPost("requests/current/sites/{siteId:int}/bes-evidence/files", AddBesEvidenceFile);
        group.MapDelete(
            "requests/current/sites/{siteId:int}/bes-evidence/files/{fileId}",
            DeleteBesEvidenceFile
        );
        group.MapPost("requests/current/submit", Submit);
    }

    internal sealed record ChangeServices(
        IAccreditationApplicationPersistence Applications,
        IChangeRequestPersistence Requests,
        CancellationToken CancellationToken
    );

    internal sealed record UploadServices(
        ICdpUploaderService CdpUploader,
        IPendingUploadService PendingUploads,
        IOptions<CdpUploaderConfig> CdpConfig,
        IOptions<AppConfig> AppConfig
    );

    internal sealed record SubmitServices(
        IValidator<SubmitRequest> Validator,
        ICaseWorkingApiAdapter CaseWorking,
        IOptions<AccreditationChangesConfig> Config,
        ILoggerFactory LoggerFactory
    );

    // Saves the request with its version check, then returns its view; 409 if someone else saved it first.
    private static async Task<IResult> SaveAndViewAsync(
        AccreditationChangeRequest request,
        AccreditationApplicationModel accreditation,
        ChangeServices services,
        bool created = false
    )
    {
        if (!await services.Requests.UpdateAsync(request, services.CancellationToken))
            return Results.Conflict(ConcurrentEditMessage);
        var view = ChangeRequestRules.ToView(request, accreditation);
        return created ? Results.Created(string.Empty, view) : Results.Ok(view);
    }

    private static async Task<(
        IResult? Error,
        AccreditationApplicationModel? Accreditation
    )> LoadGrantedAsync(string organisationId, string applicationId, ChangeServices services)
    {
        if (!ObjectId.TryParse(applicationId, out _))
            return (Results.NotFound(), null);
        var accreditation = await services.Applications.GetByIdAsync(
            organisationId,
            applicationId
        );
        if (accreditation is null)
            return (Results.NotFound(), null);
        if (accreditation.ApplicationStatus != ApplicationStatus.Approved || !accreditation.IsExporter)
            return (
                Results.Conflict(
                    "Overseas sites can only be added in-year to a granted (Approved) exporter accreditation."
                ),
                null
            );
        return (null, accreditation);
    }

    private static async Task<(
        IResult? Error,
        AccreditationApplicationModel? Accreditation,
        AccreditationChangeRequest? Draft
    )> LoadDraftAsync(string organisationId, string applicationId, ChangeServices services)
    {
        var (error, accreditation) = await LoadGrantedAsync(organisationId, applicationId, services);
        if (error is not null)
            return (error, null, null);
        var draft = await services.Requests.GetDraftAsync(
            organisationId,
            applicationId,
            services.CancellationToken
        );
        return draft is null ? (Results.NotFound(), null, null) : (null, accreditation, draft);
    }

    private static async Task<IResult> Get(
        string organisationId,
        string applicationId,
        [AsParameters] ChangeServices services
    )
    {
        var (error, accreditation) = await LoadGrantedAsync(organisationId, applicationId, services);
        if (error is not null)
            return error;

        var requests = await services.Requests.ListAsync(
            organisationId,
            applicationId,
            services.CancellationToken
        );
        // Every InProgress or Submitted request is in flight: no regulator decision flows back
        // to this service yet, so a Submitted request stays pending (AC09).
        var inFlightBySite = new Dictionary<int, AccreditationChangeRequest>();
        foreach (var request in requests)
        {
            foreach (var site in request.Sites)
                inFlightBySite.TryAdd(site.SiteId, request);
        }

        // A never-registered site has no status and is left out, as RA-425's page shows it on neither tab.
        var sites = (accreditation!.OverseasSites?.Sites ?? [])
            .Select(site => (Site: site, Status: ChangeRequestRules.AccreditationStatus(site)))
            .Where(s => s.Status is not null)
            .Select(s =>
            {
                inFlightBySite.TryGetValue(s.Site.SiteId, out var pending);
                return new AccreditationChangeSiteView(
                    s.Site.SiteId,
                    s.Site.OrsId,
                    s.Status!.Value,
                    pending?.RequestId,
                    pending?.Status,
                    ChangeRequestRules.LockedFields(s.Site),
                    s.Site
                );
            })
            .ToList();

        var draft = requests.FirstOrDefault(r => r.Status == ChangeRequestStatus.InProgress);
        return Results.Ok(
            new AccreditationChangesView(
                organisationId,
                applicationId,
                accreditation.OrganisationName,
                accreditation.RegistrationId,
                accreditation.MaterialType,
                accreditation.Year,
                accreditation.AccreditationReference,
                accreditation.RegistrationReference,
                sites,
                draft is null ? null : ChangeRequestRules.ToView(draft, accreditation),
                requests.Select(r => ChangeRequestRules.ToView(r, accreditation)).ToList(),
                requests
                    .SelectMany(r => r.Sites)
                    .Select(s => s.OrsId)
                    .OfType<string>()
                    .Distinct()
                    .ToList()
            )
        );
    }

    private static async Task<IResult> AddSite(
        string organisationId,
        string applicationId,
        AddChangeRequestSiteRequest body,
        [AsParameters] ChangeServices services
    )
    {
        var (error, accreditation) = await LoadGrantedAsync(organisationId, applicationId, services);
        if (error is not null)
            return error;

        // 404 for a site the GET view does not list (unknown, or never registered), so a client
        // cannot tell the two apart; 409 for a listed site that cannot be added in its state.
        var site = ChangeRequestRules.FindSite(accreditation!, body.SiteId);
        var status = site is null ? null : ChangeRequestRules.AccreditationStatus(site);
        if (status is null)
            return Results.NotFound();
        if (status == SiteAccreditationStatus.Accredited)
            return Results.Conflict("This overseas site is already accredited.");

        var requests = await services.Requests.ListAsync(
            organisationId,
            applicationId,
            services.CancellationToken
        );
        if (
            requests.Any(r =>
                r.Status == ChangeRequestStatus.Submitted
                && r.Sites.Exists(s => s.SiteId == body.SiteId)
            )
        )
            return Results.Conflict(
                "This overseas site already has a change request waiting for a regulator decision."
            );

        var draft = requests.FirstOrDefault(r => r.Status == ChangeRequestStatus.InProgress);
        if (draft is not null && draft.Sites.Exists(s => s.SiteId == body.SiteId))
            return Results.Ok(ChangeRequestRules.ToView(draft, accreditation!));

        var requestSite = new ChangeRequestSite
        {
            SiteId = site!.SiteId,
            OrsId = site.OrsId,
            SiteName = site.SiteName,
        };

        if (draft is null)
        {
            draft = new AccreditationChangeRequest
            {
                OrganisationId = organisationId,
                ApplicationId = applicationId,
                RegistrationId = accreditation!.RegistrationId,
                MaterialType = accreditation.MaterialType,
                Year = accreditation.Year,
                AccreditationReference = accreditation.AccreditationReference,
                Sites = [requestSite],
            };
            if (!await services.Requests.InsertAsync(draft, services.CancellationToken))
                return Results.Conflict(ConcurrentEditMessage);
        }
        else
        {
            draft.Sites.Add(requestSite);
            if (!await services.Requests.UpdateAsync(draft, services.CancellationToken))
                return Results.Conflict(ConcurrentEditMessage);
        }

        return Results.Created(string.Empty, ChangeRequestRules.ToView(draft, accreditation!));
    }

    private static async Task<IResult> RemoveSite(
        string organisationId,
        string applicationId,
        int siteId,
        [AsParameters] ChangeServices services
    )
    {
        var (error, accreditation, draft) = await LoadDraftAsync(
            organisationId,
            applicationId,
            services
        );
        if (error is not null)
            return error;
        if (draft!.Sites.RemoveAll(s => s.SiteId == siteId) == 0)
            return Results.NotFound();

        if (draft.Sites.Count == 0)
            return await services.Requests.DeleteDraftAsync(draft, services.CancellationToken)
                ? Results.NoContent()
                : Results.Conflict(ConcurrentEditMessage);

        return await SaveAndViewAsync(draft, accreditation!, services);
    }

    private static async Task<IResult> PatchSite(
        string organisationId,
        string applicationId,
        int siteId,
        PatchChangeRequestSiteRequest body,
        IValidator<PatchChangeRequestSiteRequest> validator,
        [AsParameters] ChangeServices services
    )
    {
        var validation = await validator.ValidateAsync(body, services.CancellationToken);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var (error, accreditation, draft) = await LoadDraftAsync(
            organisationId,
            applicationId,
            services
        );
        if (error is not null)
            return error;
        var requestSite = draft!.Sites.Find(s => s.SiteId == siteId);
        if (requestSite is null)
            return Results.NotFound();

        // The ticket locks these four fields once the ORS holds a value for them.
        var locked = ChangeRequestRules.LockedFields(
            ChangeRequestRules.FindSite(accreditation!, siteId)
        );
        var lockedErrors = ChangeRequestRules
            .SuppliedFields(
                new ChangeRequestSiteAnswers
                {
                    Country = body.Country,
                    Coordinates = body.Coordinates,
                    OperationCodes = body.OperationCodes,
                    ConditionsOfExport = body.ConditionsOfExport,
                }
            )
            .Where(locked.Contains)
            .ToDictionary(
                field => field,
                field => new[]
                {
                    $"{field} is already recorded for this overseas site and cannot be changed.",
                }
            );
        if (lockedErrors.Count > 0)
            return Results.ValidationProblem(lockedErrors);

        var answers = requestSite.Answers;
        answers.Country = body.Country?.Trim() ?? answers.Country;
        answers.Coordinates = body.Coordinates?.Trim() ?? answers.Coordinates;
        answers.OperationCodes = body.OperationCodes ?? answers.OperationCodes;
        answers.ConditionsOfExport = body.ConditionsOfExport ?? answers.ConditionsOfExport;

        return await SaveAndViewAsync(draft, accreditation!, services);
    }

    private static async Task<IResult> InitiateBesEvidenceUpload(
        string organisationId,
        string applicationId,
        InitiateUploadRequest body,
        [AsParameters] ChangeServices services,
        [AsParameters] UploadServices uploads
    )
    {
        var (error, _, _) = await LoadDraftAsync(organisationId, applicationId, services);
        if (error is not null)
            return error;

        return await AccreditationApplicationEndpoints.StartCdpUploadAsync(
            organisationId,
            applicationId,
            body,
            uploads.CdpConfig.Value.BesEvidenceBucket,
            new(uploads.CdpUploader, uploads.PendingUploads, uploads.AppConfig.Value),
            services.CancellationToken
        );
    }

    private static async Task<IResult> AddBesEvidenceFile(
        string organisationId,
        string applicationId,
        int siteId,
        AddBesEvidenceFileRequest body,
        IValidator<AddBesEvidenceFileRequest> validator,
        [AsParameters] ChangeServices services,
        [AsParameters] UploadServices uploads
    )
    {
        var validation = await validator.ValidateAsync(body, services.CancellationToken);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var (error, accreditation, draft) = await LoadDraftAsync(
            organisationId,
            applicationId,
            services
        );
        if (error is not null)
            return error;
        var requestSite = draft!.Sites.Find(s => s.SiteId == siteId);
        if (requestSite is null)
            return Results.NotFound();

        // 422, as the other request rules here: a site inside the EU or OECD, or whose conditions
        // of export are met, takes no BES evidence.
        var ors = ChangeRequestRules.FindSite(accreditation!, siteId);
        if (!ChangeRequestRules.IsBesEvidenceRequired(ors, ChangeRequestRules.Effective(ors, requestSite.Answers)))
            return Results.UnprocessableEntity(
                "This overseas site does not need evidence of broadly equivalent standards."
            );

        var (uploadError, scannedFile) =
            await AccreditationApplicationEndpoints.TryResolveScannedFileAsync(
                body.FileUploadId,
                uploads.PendingUploads,
                services.CancellationToken
            );
        if (uploadError is not null)
            return uploadError;

        // Same cap as RA-605 on the application, at the same point.
        var overCap = AccreditationApplicationEndpoints.BesEvidenceFileCapRejection(requestSite.BesEvidence);
        if (overCap is not null)
            return overCap;

        // Same rule as RA-571 on the application: no two files in the request share a name.
        if (
            draft
                .Sites.SelectMany(s => s.BesEvidence?.BesEvidenceUploads ?? [])
                .Any(f => string.Equals(f.Filename, scannedFile!.Filename, StringComparison.OrdinalIgnoreCase))
        )
            return Results.UnprocessableEntity(DuplicateFilenameGuard.DuplicateFilenameMessage);

        requestSite.BesEvidence ??= new BesEvidenceModel();
        requestSite.BesEvidence.BesEvidenceUploads.Add(
            new BesEvidenceFileModel
            {
                FileId = scannedFile!.FileId!,
                Filename = scannedFile.Filename!,
                ContentType = scannedFile.ContentType ?? scannedFile.DetectedContentType,
                ScanStatus = AccreditationApplicationEndpoints.ScanStatusOf(scannedFile).ToString(),
                BesEvidenceValidFromDate = body.BesEvidenceValidFromDate,
                BesEvidenceExpiryDate = body.BesEvidenceExpiryDate,
                S3Key = scannedFile.S3Key!,
                S3Bucket = scannedFile.S3Bucket,
            }
        );

        return await SaveAndViewAsync(draft, accreditation!, services, created: true);
    }

    private static async Task<IResult> DeleteBesEvidenceFile(
        string organisationId,
        string applicationId,
        int siteId,
        string fileId,
        [AsParameters] ChangeServices services
    )
    {
        var (error, accreditation, draft) = await LoadDraftAsync(
            organisationId,
            applicationId,
            services
        );
        if (error is not null)
            return error;
        var uploads = draft!.Sites.Find(s => s.SiteId == siteId)?.BesEvidence?.BesEvidenceUploads;
        if (uploads is null || uploads.RemoveAll(f => f.FileId == fileId) == 0)
            return Results.NotFound();

        return await SaveAndViewAsync(draft, accreditation!, services);
    }

    private static async Task<IResult> Submit(
        string organisationId,
        string applicationId,
        SubmitRequest body,
        [AsParameters] ChangeServices services,
        [AsParameters] SubmitServices submit
    )
    {
        var validation = await submit.Validator.ValidateAsync(body, services.CancellationToken);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var (error, accreditation, draft) = await LoadDraftAsync(
            organisationId,
            applicationId,
            services
        );
        if (error is not null)
            return error;

        // AC02: nothing is submitted while any site is missing required information.
        var incomplete = draft!
            .Sites.SelectMany(requestSite =>
                ChangeRequestRules
                    .MissingFields(
                        ChangeRequestRules.FindSite(accreditation!, requestSite.SiteId),
                        requestSite
                    )
                    .Select(field =>
                        (
                            Key: $"sites[{requestSite.SiteId}].{field}",
                            Message: ChangeRequestRules.MissingFieldMessages[field]
                        )
                    )
            )
            .ToDictionary(e => e.Key, e => new[] { e.Message });
        if (draft.Sites.Count == 0)
            incomplete["sites"] = ["Add at least one overseas reprocessing site."];
        if (incomplete.Count > 0)
            return Results.ValidationProblem(
                incomplete,
                title: "The change request is incomplete.",
                statusCode: StatusCodes.Status422UnprocessableEntity
            );

        draft.SubmittedBy = new SubmittedByModel
        {
            FullName = body.FullName,
            JobTitle = body.JobTitle,
            Email = body.Email,
        };
        draft.PaymentReference = body.PaymentReference;
        draft.ChargePence = ChangeRequestRules.Charge(draft.Sites.Count).TotalPence;
        draft.Status = ChangeRequestStatus.Submitted;
        draft.SubmittedAt = DateTime.UtcNow;

        // Saved, with its version check, before Case Management hears of it: of two concurrent
        // submits only one gets past here, so a lost race never creates a work item.
        if (!await services.Requests.UpdateAsync(draft, services.CancellationToken))
            return Results.Conflict(ConcurrentEditMessage);

        if (!submit.Config.Value.SubmitToCaseManagement)
            return Results.Ok(ChangeRequestRules.ToView(draft, accreditation!));

        var logger = submit.LoggerFactory.CreateLogger(typeof(AccreditationChangeEndpoints));
        CaseWorkingSubmissionResult result;
        try
        {
            // The payload's operatorRequestId is this request's id, the same on every attempt, so
            // Case Management can recognise a retry after a work item it created but whose response
            // never arrived (a timeout).
            result = await submit.CaseWorking.SubmitChangeRequestAsync(
                draft,
                accreditation!,
                services.CancellationToken
            );
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the request goes back to InProgress, so its sites are not held
            // by a request the regulator never received. Only Case Management's own failures are
            // a 503; anything else is a defect and goes to the exception handler.
            await ReopenAsync(draft, services, logger);
            if (
                ex
                is not (
                    HttpRequestException
                    or CaseWorkingApiTimeoutException
                    or CaseWorkingNotConfiguredException
                )
            )
                throw;
            logger.LogError(
                ex,
                "Case Management did not accept change request {RequestId}; it is back InProgress",
                draft.RequestId
            );
            return Results.Problem(
                detail: "The change request could not be sent to the regulator. Try again.",
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }

        draft.CaseManagementReference = result.ApplicationReference;
        draft.CaseManagementWorkItemId = result.WorkItemId;
        // Nothing else writes a Submitted request, so this only fails if the store does; the
        // submission itself has succeeded either way.
        if (!await services.Requests.UpdateAsync(draft, services.CancellationToken))
            logger.LogError(
                "Change request {RequestId} was sent to Case Management as {Reference} but the reference was not saved",
                draft.RequestId,
                result.ApplicationReference
            );
        return Results.Ok(ChangeRequestRules.ToView(draft, accreditation!));
    }

    // Undoes Submit's save after Case Management failed. Not cancellable: the caller may have gone,
    // and the request must not be left Submitted.
    private static async Task ReopenAsync(
        AccreditationChangeRequest draft,
        ChangeServices services,
        ILogger logger
    )
    {
        draft.Status = ChangeRequestStatus.InProgress;
        draft.SubmittedAt = null;
        draft.SubmittedBy = null;
        draft.PaymentReference = null;
        draft.ChargePence = null;
        // Fails only if another draft was started for the accreditation in the moment the request
        // was Submitted, which the one-draft rule then refuses.
        if (!await services.Requests.UpdateAsync(draft, CancellationToken.None))
            logger.LogError(
                "Change request {RequestId} could not be reopened after Case Management failed; it stays Submitted without a work item",
                draft.RequestId
            );
    }
}
