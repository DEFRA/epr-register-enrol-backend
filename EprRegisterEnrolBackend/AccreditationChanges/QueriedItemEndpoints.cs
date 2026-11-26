using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-698: the regulator's query on an in-year change request item, and the operator's response to it.
/// <para>
/// Case Management raises the query (<see cref="QueryRoute"/>); the item becomes Queried. While it is, and only
/// then, the operator may change that item's editable fields and add BES files through the item-keyed routes
/// below, and answer with <c>resubmit</c>, which tells ManagementBe and moves the item to Updated. A queried item
/// sits in a Submitted request, so none of this uses the draft's <c>requests/current</c> routes, and no other item
/// or ORS of the request can be changed.
/// </para>
/// </summary>
public static class QueriedItemEndpoints
{
    internal const string QueryRoute = "api/v1/accreditation-changes/case-management/{workItemId}/query";

    private const int MaxAttempts = 3;

    private const string NotQueriedMessage = "This change request item has not been queried, so it cannot be changed.";

    /// <summary>The item-keyed routes, in the accreditation's group.</summary>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPatch("items/{itemId:guid}/answers", PatchAnswers);
        group.MapPatch("items/{itemId:guid}/interim-sites/{interimSiteId:int}/answers", PatchInterimSiteAnswers);
        group.MapPost("items/{itemId:guid}/bes-evidence/initiate", InitiateBesEvidenceUpload);
        group.MapPost("items/{itemId:guid}/bes-evidence/files", AddBesEvidenceFile);
        group.MapDelete("items/{itemId:guid}/bes-evidence/files/{fileId}", DeleteBesEvidenceFile);
        group.MapPost("items/{itemId:guid}/resubmit", Resubmit);
    }

    public static void MapCaseManagement(IEndpointRouteBuilder app) =>
        app.MapPost(QueryRoute, QueryFromCaseManagement).RequireCaseManagementCaller();

    // A queried item of a request the accreditation holds, with the ORS it is for. 404 for an unknown item, 409 when it
    // is not Queried: the only state in which anything on it may change.
    private static async Task<(
        IResult? Error,
        AccreditationApplicationModel? Accreditation,
        AccreditationChangeRequest? Request,
        ChangeRequestSite? Item,
        OverseasSiteModel? Site
    )> LoadQueriedAsync(
        string organisationId,
        string applicationId,
        Guid itemId,
        AccreditationChangeEndpoints.ChangeServices services
    )
    {
        var (error, accreditation) = await AccreditationChangeEndpoints.LoadGrantedExporterAsync(
            organisationId,
            applicationId,
            services
        );
        if (error is not null)
            return (error, null, null, null, null);
        var requests = await services.Requests.ListAsync(organisationId, applicationId, services.CancellationToken);
        var request = requests.Find(r => r.Sites.Exists(s => s.ItemId == itemId));
        var item = request?.Sites.Find(s => s.ItemId == itemId);
        if (request is null || item is null)
            return (Results.NotFound(), null, null, null, null);
        if (item.Status != ChangeItemStatus.Queried || item.Query is null)
            return (Results.Conflict(NotQueriedMessage), null, null, null, null);
        var site = ChangeRequestRules.FindSite(accreditation!, item.SiteId);
        if (site is null)
            return (Results.Conflict("The overseas site is no longer on the accreditation."), null, null, null, null);
        return (null, accreditation, request, item, site);
    }

    private static async Task<IResult> PatchAnswers(
        string organisationId,
        string applicationId,
        Guid itemId,
        PatchQueriedSiteRequest body,
        IValidator<PatchQueriedSiteRequest> validator,
        [AsParameters] AccreditationChangeEndpoints.ChangeServices services
    )
    {
        var locked = QueriedItemRules.LockedViolations(body, interim: false);
        if (locked.Count > 0)
            return Results.ValidationProblem(locked, statusCode: StatusCodes.Status422UnprocessableEntity);
        var validation = await validator.ValidateAsync(body, services.CancellationToken);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var (error, accreditation, request, item, site) = await LoadQueriedAsync(
            organisationId,
            applicationId,
            itemId,
            services
        );
        if (error is not null)
            return error;

        QueriedItemRules.Apply(item!.Answers, body, site!);
        return await AccreditationChangeEndpoints.SaveAndViewAsync(request!, accreditation!, services);
    }

    private static async Task<IResult> PatchInterimSiteAnswers(
        string organisationId,
        string applicationId,
        Guid itemId,
        int interimSiteId,
        PatchQueriedSiteRequest body,
        IValidator<PatchQueriedSiteRequest> validator,
        [AsParameters] AccreditationChangeEndpoints.ChangeServices services
    )
    {
        var locked = QueriedItemRules.LockedViolations(body, interim: true);
        if (locked.Count > 0)
            return Results.ValidationProblem(locked, statusCode: StatusCodes.Status422UnprocessableEntity);
        var validation = await validator.ValidateAsync(body, services.CancellationToken);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var (error, accreditation, request, item, site) = await LoadQueriedAsync(
            organisationId,
            applicationId,
            itemId,
            services
        );
        if (error is not null)
            return error;

        // No adding or removing interim sites: only an active one of this ORS can be answered.
        var interim = QueriedItemRules.ActiveInterimSites(site).FirstOrDefault(s => s.SiteId == interimSiteId);
        if (interim is null)
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["interimSiteId"] = ["This is not an active interim site of the queried overseas site."],
                },
                statusCode: StatusCodes.Status422UnprocessableEntity
            );

        var answers = item!.Answers.InterimSites.Find(a => a.SiteId == interimSiteId);
        if (answers is null)
        {
            answers = new ChangeRequestInterimSiteAnswers { SiteId = interimSiteId };
            item.Answers.InterimSites.Add(answers);
        }
        QueriedItemRules.Apply(answers, body, interim);
        // An interim site back at its own values holds no answers.
        item.Answers.InterimSites.RemoveAll(a => a.SiteId == interimSiteId && !HasAnswer(a));
        return await AccreditationChangeEndpoints.SaveAndViewAsync(request!, accreditation!, services);
    }

    private static bool HasAnswer(ChangeRequestInterimSiteAnswers answers) =>
        QueriedItemRules.InterimFields.Any(f => f.Get(answers) is not null);

    private static async Task<IResult> InitiateBesEvidenceUpload(
        string organisationId,
        string applicationId,
        Guid itemId,
        InitiateUploadRequest body,
        [AsParameters] AccreditationChangeEndpoints.ChangeServices services,
        [AsParameters] AccreditationChangeEndpoints.UploadServices uploads
    )
    {
        var (error, _, _, _, _) = await LoadQueriedAsync(organisationId, applicationId, itemId, services);
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
        Guid itemId,
        AddBesEvidenceFileRequest body,
        IValidator<AddBesEvidenceFileRequest> validator,
        [AsParameters] AccreditationChangeEndpoints.ChangeServices services,
        [AsParameters] AccreditationChangeEndpoints.UploadServices uploads
    )
    {
        var validation = await validator.ValidateAsync(body, services.CancellationToken);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var (error, accreditation, request, item, site) = await LoadQueriedAsync(
            organisationId,
            applicationId,
            itemId,
            services
        );
        if (error is not null)
            return error;

        var (uploadError, scannedFile) = await AccreditationApplicationEndpoints.TryResolveScannedFileAsync(
            body.FileUploadId,
            uploads.PendingUploads,
            services.CancellationToken
        );
        if (uploadError is not null)
            return uploadError;

        // The same caps as the draft's upload: across the files the site holds and the item's, and no file in the
        // accreditation or in this request may share the upload's name.
        var overCap = AccreditationApplicationEndpoints.BesEvidenceFileCapRejection(
            ChangeRequestRules.GrantedBesEvidence(site, item!.BesEvidence)
        );
        if (overCap is not null)
            return overCap;
        var existingFilenames = DuplicateFilenameGuard
            .ExistingFilenames(accreditation!)
            .Concat(request!.Sites.SelectMany(s => s.BesEvidence?.BesEvidenceUploads ?? []).Select(f => f.Filename));
        if (DuplicateFilenameGuard.IsDuplicate(scannedFile!.Filename, existingFilenames))
            return AccreditationApplicationEndpoints.DuplicateFilenameRejection();

        item.BesEvidence ??= new BesEvidenceModel();
        item.BesEvidence.BesEvidenceUploads.Add(
            new BesEvidenceFileModel
            {
                FileId = scannedFile.FileId!,
                Filename = scannedFile.Filename!,
                ContentType = scannedFile.ContentType ?? scannedFile.DetectedContentType,
                ScanStatus = AccreditationApplicationEndpoints.ScanStatusOf(scannedFile).ToString(),
                BesEvidenceValidFromDate = body.BesEvidenceValidFromDate,
                BesEvidenceExpiryDate = body.BesEvidenceExpiryDate,
                S3Key = scannedFile.S3Key!,
                S3Bucket = scannedFile.S3Bucket,
            }
        );

        return await AccreditationChangeEndpoints.SaveAndViewAsync(request, accreditation!, services, created: true);
    }

    private static async Task<IResult> DeleteBesEvidenceFile(
        string organisationId,
        string applicationId,
        Guid itemId,
        string fileId,
        [AsParameters] AccreditationChangeEndpoints.ChangeServices services
    )
    {
        var (error, accreditation, request, item, _) = await LoadQueriedAsync(
            organisationId,
            applicationId,
            itemId,
            services
        );
        if (error is not null)
            return error;

        // Only a file added in response to the query can be removed.
        if (item!.Query!.BesFileIdsAtQuery.Contains(fileId))
            return Results.UnprocessableEntity("A file uploaded before the query cannot be removed.");
        var uploads = item.BesEvidence?.BesEvidenceUploads;
        if (uploads is null || uploads.RemoveAll(f => f.FileId == fileId) == 0)
            return Results.NotFound();

        return await AccreditationChangeEndpoints.SaveAndViewAsync(request!, accreditation!, services);
    }

    private static async Task<IResult> Resubmit(
        string organisationId,
        string applicationId,
        Guid itemId,
        ResubmitItemRequest body,
        IValidator<ResubmitItemRequest> validator,
        ICaseWorkingApiAdapter caseWorking,
        TimeProvider clock,
        [AsParameters] AccreditationChangeEndpoints.ChangeServices services
    )
    {
        var validation = await validator.ValidateAsync(body, services.CancellationToken);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var (error, accreditation, request, item, site) = await LoadQueriedAsync(
            organisationId,
            applicationId,
            itemId,
            services
        );
        if (error is not null)
            return error;

        var changed = QueriedItemRules.ChangedFields(item!.Answers, item.Query!.AnswersAtQuery);
        var newFiles = QueriedItemRules.NewBesFiles(item);
        if (changed.Count == 0 && newFiles.Count == 0)
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["changes"] = ["Change at least one field or upload a BES file before you resubmit."],
                },
                title: "Nothing has changed.",
                statusCode: StatusCodes.Status422UnprocessableEntity
            );
        // Required BES evidence still has to be in place and scanned clean.
        var effective = ChangeRequestRules.Effective(site, item.Answers);
        if (
            ChangeRequestRules.IsBesEvidenceRequired(site, effective)
            && !ChangeRequestRules.HasCleanBesEvidence(item.BesEvidence)
        )
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    [ChangeRequestRules.BesEvidence] = [ChangeRequestRules.MissingFieldMessages[ChangeRequestRules.BesEvidence]],
                },
                statusCode: StatusCodes.Status422UnprocessableEntity
            );

        var now = clock.GetUtcNow().UtcDateTime;
        var current = QueriedItemRules.Values(item.Answers);
        var response = new ChangeRequestQueryResponse(
            now,
            current
                .Where(kv => !kv.Key.StartsWith("interimSites[", StringComparison.Ordinal) && changed.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            [
                .. item.Answers.InterimSites.Select(a => new InterimSiteResponse(
                    a.SiteId,
                    QueriedItemRules.ActiveInterimSites(site).First(s => s.SiteId == a.SiteId).SiteNumber,
                    QueriedItemRules
                        .InterimFields.Where(f =>
                            changed.Contains(QueriedItemRules.InterimKey(a.SiteId, f.Key))
                            && f.Get(a) is not null
                        )
                        .ToDictionary(f => f.Key, f => f.Get(a))
                )).Where(s => s.Changes.Count > 0),
            ],
            newFiles
        );

        // ManagementBe first, as for an application: the item is Updated only once the regulator's side knows.
        var contact = new QuerySubmitterContactDetails
        {
            FullName = body.FullName,
            Email = body.Email ?? request!.SubmittedBy?.Email ?? string.Empty,
            Role = body.JobTitle,
        };
        var result = await caseWorking.ResumeChangeRequestItemFromQueryAsync(
            item,
            contact,
            response,
            services.CancellationToken
        );
        if (!result.IsSuccess)
            return Results.Problem(
                detail: "Your response could not be sent to the regulator. Try again.",
                statusCode: StatusCodes.Status503ServiceUnavailable
            );

        item.Query.QuerySubmissions.Add(
            new QuerySubmission
            {
                SubmittedAt = now,
                FullName = body.FullName,
                JobTitle = body.JobTitle,
                ChangedFields = changed,
                NewBesFileIds = [.. newFiles.Select(f => f.FileId)],
            }
        );
        item.Status = ChangeItemStatus.Updated;
        return await AccreditationChangeEndpoints.SaveAndViewAsync(request!, accreditation!, services);
    }

    private static async Task<IResult> QueryFromCaseManagement(
        Guid workItemId,
        QueryFromCaseManagementRequest body,
        IValidator<QueryFromCaseManagementRequest> validator,
        IChangeRequestPersistence persistence,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var validation = await validator.ValidateAsync(body, cancellationToken);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());
        var logger = loggerFactory.CreateLogger(typeof(QueriedItemEndpoints));

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var request = await persistence.GetByCaseManagementWorkItemIdAsync(workItemId, cancellationToken);
            var item = request?.Sites.Find(s => s.CaseManagementWorkItemId == workItemId);
            if (request is null || item is null)
                return Results.NotFound();

            // One open query per item; and only an item the regulator is assessing can be queried.
            if (item.Status == ChangeItemStatus.Queried)
                return Results.Conflict("The change request item already has an open query.");
            if (item.Status is not (ChangeItemStatus.Submitted or ChangeItemStatus.DulyMade or ChangeItemStatus.Updated))
                return Results.Conflict(
                    $"A change request item that is {item.Status} cannot be queried: it must be Submitted, DulyMade or Updated."
                );

            item.Query = new ItemQuery
            {
                QueryNote = body.Note.Trim(),
                RaisedAt = body.RaisedAt ?? clock.GetUtcNow().UtcDateTime,
                BesFileIdsAtQuery = [.. (item.BesEvidence?.BesEvidenceUploads ?? []).Select(f => f.FileId)],
                AnswersAtQuery = QueriedItemRules.Clone(item.Answers),
                QuerySubmissions = item.Query?.QuerySubmissions ?? [],
            };
            item.Status = ChangeItemStatus.Queried;

            if (await persistence.UpdateAsync(request, cancellationToken))
            {
                logger.LogInformation(
                    "Change request item {ItemId} queried for workItemId={WorkItemId}",
                    item.ItemId,
                    workItemId
                );
                return Results.Ok(item);
            }
        }

        return Results.Problem(
            detail: "The change request was being changed at the same time. Try again.",
            statusCode: StatusCodes.Status503ServiceUnavailable
        );
    }
}
