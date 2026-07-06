using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationApplication.Endpoints;

public static class AccreditationApplicationEndpoints
{
    public static void UseAccreditationApplicationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/v1/accreditation-applications");

        group.MapPost("{organisationId}/{registrationId}/{materialType}/seed", Seed);
        group.MapGet("{organisationId}", GetList);
        group.MapGet("{organisationId}/{applicationId}", GetById);
        group.MapPatch("{organisationId}/{applicationId}/prns", PatchPrns);
        group.MapPatch("{organisationId}/{applicationId}/tonnage", PatchTonnage);
        group.MapPatch("{organisationId}/{applicationId}/business-plan", PatchBusinessPlan);
        group.MapPatch("{organisationId}/{applicationId}/sampling-plan", PatchSamplingPlan);
        group.MapPatch("{organisationId}/{applicationId}/overseas-sites", PatchOverseasSites);
        group.MapPost(
            "{organisationId}/{applicationId}/overseas-sites/{siteId}/bes-evidence/files",
            AddBesEvidenceFile
        );
        group.MapPatch(
            "{organisationId}/{applicationId}/overseas-sites/{siteId}/bes-evidence",
            PatchBesEvidence
        );
        group.MapDelete(
            "{organisationId}/{applicationId}/overseas-sites/{siteId}/bes-evidence/files/{fileId}",
            DeleteBesEvidenceFile
        );
        group.MapPatch("{organisationId}/{applicationId}/bes-evidence", PatchBesEvidenceSection);
        group.MapPost("{organisationId}/{applicationId}/submit", Submit);
        group.MapPost("{organisationId}/{applicationId}/files", AddFile);
        group.MapDelete("{organisationId}/{applicationId}/files/{fileId}", DeleteFile);
        group.MapPost("{organisationId}/{applicationId}/approve", Approve);
        group.MapPost("{organisationId}/{applicationId}/reject", Reject);
        group.MapPost("{organisationId}/{applicationId}/files/initiate", InitiateUpload);
        group.MapPost(
            "{organisationId}/{applicationId}/files/bes-evidence/initiate",
            InitiateBesEvidenceUpload
        );
        group.MapPost("files/upload-completed", UploadCompleted);
        group.MapGet(
            "{organisationId}/{applicationId}/files/{fileUploadId}/status",
            GetUploadStatus
        );
    }

    private static async Task<IResult> Seed(
        string organisationId,
        string registrationId,
        string materialType,
        SeedRequest request,
        IAccreditationApplicationPersistence persistence,
        IReExApiAdapter reExAdapter,
        IValidator<SeedRequest> validator
    )
    {
        if (!Enum.TryParse<MaterialType>(materialType, out var materialTypeEnum))
            return Results.BadRequest("Invalid material type.");

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        // Idempotency check before the ReEx call — duplicate seeds return the existing document.
        var existing = (await persistence.GetByOrganisationAsync(organisationId)).FirstOrDefault(
            a =>
                a.RegistrationId == registrationId
                && a.MaterialType == materialTypeEnum
                && a.Year == request.Year
        );
        if (existing is not null)
            return Results.Ok(existing);

        var adapterResult = await reExAdapter.GetAccreditationAsync(
            organisationId,
            registrationId,
            materialTypeEnum,
            request.Year - 1
        );

        if (!adapterResult.IsSuccess)
            return adapterResult.IsNotFound
                ? Results.NotFound()
                : Results.Problem(statusCode: adapterResult.IsUpstreamFailure ? 502 : 503);

        var priorYearData = adapterResult.Value!;

        var application = new AccreditationApplicationModel
        {
            OrganisationId = organisationId,
            OrganisationName = priorYearData.OrganisationName,
            Year = request.Year,
            RegistrationId = registrationId,
            IsExporter = priorYearData.IsExporter,
            SiteAddress = priorYearData.SiteAddress,
            RegistrationReference = priorYearData.RegistrationReference,
            MaterialType = materialTypeEnum,
            ApplicationStatus = ApplicationStatus.Saved,
            SourceReExAccreditationId = priorYearData.AccreditationId,
            SourceYear = request.Year - 1,
            OverseasSites = priorYearData.IsExporter
                ? new AccreditationApplicationOverseasSites { Sites = priorYearData.OverseasSites }
                : null,
        };

        if (priorYearData.Prns != null)
        {
            application.Prns = new AccreditationApplicationPrns
            {
                PlannedTonnageBand = priorYearData.Prns.PlannedTonnageBand,
                Authorisers = priorYearData.Prns.Authorisers,
                SectionStatus = SectionStatus.NotStarted,
            };
        }

        if (priorYearData.BusinessPlan != null)
        {
            application.BusinessPlan = new AccreditationApplicationBusinessPlan
            {
                NewInfrastructurePercent = priorYearData.BusinessPlan.NewInfrastructurePercent,
                PriceSupportPercent = priorYearData.BusinessPlan.PriceSupportPercent,
                BusinessCollectionsPercent = priorYearData.BusinessPlan.BusinessCollectionsPercent,
                CommunicationsPercent = priorYearData.BusinessPlan.CommunicationsPercent,
                NewMarketsPercent = priorYearData.BusinessPlan.NewMarketsPercent,
                NewUsesPercent = priorYearData.BusinessPlan.NewUsesPercent,
                SectionStatus = SectionStatus.NotStarted,
            };
        }

        application.DateLastEdited = application.CreatedAt;

        var created = await persistence.CreateAsync(application);
        if (created is null)
            return Results.Problem("Failed to create accreditation application.");

        return Results.Created(
            $"/api/v1/accreditation-applications/{organisationId}/{created.Id}",
            created
        );
    }

    private static async Task<IResult> GetList(
        string organisationId,
        IAccreditationApplicationPersistence persistence
    )
    {
        var applications = await persistence.GetByOrganisationAsync(organisationId);
        return Results.Ok(applications);
    }

    private static async Task<IResult> GetById(
        string organisationId,
        string applicationId,
        IAccreditationApplicationPersistence persistence,
        ICaseWorkingApiAdapter caseWorkingAdapter,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        // Skip the round-trip entirely when there is nothing to look up (e.g. older
        // applications submitted before the work-item id was persisted, or applications
        // never submitted at all). Defence in depth otherwise: GetNotificationStatusAsync
        // already guarantees it never throws, but this lookup must never be able to fail
        // the response regardless of adapter implementation (RA102-j7s).
        if (application.CaseManagementWorkItemId is not null)
        {
            try
            {
                application.NotificationStatus =
                    await caseWorkingAdapter.GetNotificationStatusAsync(
                        application,
                        cancellationToken
                    );
            }
            catch (Exception ex)
            {
                loggerFactory
                    .CreateLogger("AccreditationApplicationEndpoints")
                    .LogWarning(
                        ex,
                        "Failed to resolve notification status for applicationId={ApplicationId}",
                        applicationId
                    );
            }
        }

        return Results.Ok(application);
    }

    private static async Task<IResult> PatchPrns(
        string organisationId,
        string applicationId,
        PatchPrnsRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<PatchPrnsRequest> validator
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        if (request.PlannedTonnageBand.HasValue)
            application.Prns.PlannedTonnageBand = request.PlannedTonnageBand;

        if (request.Authorisers != null)
            application.Prns.Authorisers = request.Authorisers;

        application.Prns.SectionStatus = SectionStatusService.ComputePrns(application.Prns);
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update PRNs section.")
            : Results.Ok(updated);
    }

    private static async Task<IResult> PatchTonnage(
        string organisationId,
        string applicationId,
        PatchTonnageRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<PatchTonnageRequest> validator
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        if (request.PlannedTonnageBand.HasValue)
            application.Prns.PlannedTonnageBand = request.PlannedTonnageBand;

        if (request.Authorisers != null)
            application.Prns.Authorisers = request.Authorisers;

        application.Prns.SectionStatus = SectionStatusService.ComputePrns(application.Prns);
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null ? Results.Problem("Failed to update tonnage.") : Results.Ok(updated);
    }

    private static async Task<IResult> PatchBusinessPlan(
        string organisationId,
        string applicationId,
        PatchBusinessPlanRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<PatchBusinessPlanRequest> validator
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.UnprocessableEntity(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        var bp = application.BusinessPlan;
        if (request.NewInfrastructurePercent.HasValue)
            bp.NewInfrastructurePercent = request.NewInfrastructurePercent;
        if (request.PriceSupportPercent.HasValue)
            bp.PriceSupportPercent = request.PriceSupportPercent;
        if (request.BusinessCollectionsPercent.HasValue)
            bp.BusinessCollectionsPercent = request.BusinessCollectionsPercent;
        if (request.CommunicationsPercent.HasValue)
            bp.CommunicationsPercent = request.CommunicationsPercent;
        if (request.NewMarketsPercent.HasValue)
            bp.NewMarketsPercent = request.NewMarketsPercent;
        if (request.NewUsesPercent.HasValue)
            bp.NewUsesPercent = request.NewUsesPercent;

        if (request.NewInfrastructureDetail != null)
            bp.NewInfrastructureDetail = request.NewInfrastructureDetail;
        if (request.PriceSupportDetail != null)
            bp.PriceSupportDetail = request.PriceSupportDetail;
        if (request.BusinessCollectionsDetail != null)
            bp.BusinessCollectionsDetail = request.BusinessCollectionsDetail;
        if (request.CommunicationsDetail != null)
            bp.CommunicationsDetail = request.CommunicationsDetail;
        if (request.NewMarketsDetail != null)
            bp.NewMarketsDetail = request.NewMarketsDetail;
        if (request.NewUsesDetail != null)
            bp.NewUsesDetail = request.NewUsesDetail;

        bp.SectionStatus = SectionStatusService.ComputeBusinessPlan(bp);
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update business plan section.")
            : Results.Ok(updated);
    }

    private static async Task<IResult> PatchSamplingPlan(
        string organisationId,
        string applicationId,
        PatchSamplingPlanRequest request,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        if (request.Files != null)
            application.SamplingPlan.Files = request.Files;

        application.SamplingPlan.SectionStatus = SectionStatusService.ComputeSamplingPlan(
            application.SamplingPlan
        );
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update sampling plan section.")
            : Results.Ok(updated);
    }

    private static async Task<IResult> PatchOverseasSites(
        string organisationId,
        string applicationId,
        PatchOverseasSitesRequest request,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        if (application.OverseasSites is null)
            application.OverseasSites = new AccreditationApplicationOverseasSites();

        if (request.Sites != null)
            application.OverseasSites.Sites = request.Sites;

        application.OverseasSites.SectionStatus = application.OverseasSites.Sites.Any(s =>
            s.Selected
        )
            ? SectionStatus.Completed
            : SectionStatus.NotStarted;

        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update overseas sites.")
            : Results.Ok(updated);
    }

    private static async Task<IResult> AddBesEvidenceFile(
        string organisationId,
        string applicationId,
        int siteId,
        AddBesEvidenceFileRequest request,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        var site = application.OverseasSites?.Sites.FirstOrDefault(s => s.SiteId == siteId);
        if (site is null)
            return Results.NotFound();

        site.BesEvidence ??= new BesEvidenceModel();
        site.BesEvidence.BesEvidenceUploads.Add(
            new BesEvidenceFileModel
            {
                FileId = request.FileId,
                Filename = request.Filename,
                ContentType = request.ContentType,
                ScanStatus = request.ScanStatus,
                BesEvidenceValidFromDate = request.BesEvidenceValidFromDate,
                BesEvidenceExpiryDate = request.BesEvidenceExpiryDate,
            }
        );

        application.DateLastEdited = DateTime.UtcNow;
        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to add BES evidence file.")
            : Results.Created(string.Empty, site.BesEvidence);
    }

    private static async Task<IResult> PatchBesEvidence(
        string organisationId,
        string applicationId,
        int siteId,
        PatchBesEvidenceRequest request,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        var site = application.OverseasSites?.Sites.FirstOrDefault(s => s.SiteId == siteId);
        if (site is null)
            return Results.NotFound();

        site.BesEvidence ??= new BesEvidenceModel();
        if (request.DoYouWantToUploadMoreEvidence.HasValue)
            site.BesEvidence.DoYouWantToUploadMoreEvidence = request
                .DoYouWantToUploadMoreEvidence
                .Value;

        application.DateLastEdited = DateTime.UtcNow;
        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update BES evidence.")
            : Results.Ok(updated);
    }

    private static async Task<IResult> DeleteBesEvidenceFile(
        string organisationId,
        string applicationId,
        int siteId,
        string fileId,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        var site = application.OverseasSites?.Sites.FirstOrDefault(s => s.SiteId == siteId);
        if (site?.BesEvidence is null)
            return Results.NotFound();

        var removed = site.BesEvidence.BesEvidenceUploads.RemoveAll(f => f.FileId == fileId);
        if (removed == 0)
            return Results.NotFound();

        application.DateLastEdited = DateTime.UtcNow;
        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to delete BES evidence file.")
            : Results.Ok();
    }

    private static async Task<IResult> PatchBesEvidenceSection(
        string organisationId,
        string applicationId,
        PatchBesEvidenceSectionRequest request,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        application.BesEvidence ??= new AccreditationApplicationBesEvidence();
        if (request.SectionStatus.HasValue)
            application.BesEvidence.SectionStatus = request.SectionStatus.Value;

        application.DateLastEdited = DateTime.UtcNow;
        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update BES evidence section.")
            : Results.Ok(updated);
    }

    private static async Task<IResult> Submit(
        string organisationId,
        string applicationId,
        SubmitRequest request,
        IAccreditationApplicationPersistence persistence,
        ICaseWorkingApiAdapter caseWorkingAdapter,
        IValidator<SubmitRequest> validator,
        CancellationToken cancellationToken
    )
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        if (application.ApplicationStatus == ApplicationStatus.Sent)
            return Results.Ok(
                new SubmitResponse
                {
                    AccreditationReference = application.ApplicationReference,
                    CaseManagementReference = application.CaseManagementReference,
                }
            );

        if (application.ApplicationStatus != ApplicationStatus.Started)
            return Results.Conflict("Application must be in 'Started' status to submit.");

        if (
            application.Prns.SectionStatus != SectionStatus.Completed
            || application.BusinessPlan.SectionStatus != SectionStatus.Completed
            || application.SamplingPlan.SectionStatus != SectionStatus.Completed
        )
        {
            return Results.BadRequest("All sections must be completed before submission.");
        }

        application.ApplicationStatus = ApplicationStatus.Sent;
        application.DateSent = DateTime.UtcNow;
        application.DateLastEdited = DateTime.UtcNow;
        application.SubmittedBy = new SubmittedByModel
        {
            FullName = request.FullName,
            JobTitle = request.JobTitle,
            Email = request.Email,
        };

        // Call adapter before persisting: if adapter fails, DB is unchanged and the caller can retry safely.
        var submissionResult = await caseWorkingAdapter.SubmitApplicationAsync(
            application,
            cancellationToken
        );
        application.ApplicationReference = submissionResult.ApplicationReference;
        application.CaseManagementReference = submissionResult.ApplicationReference;
        application.CaseManagementWorkItemId = submissionResult.WorkItemId;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to submit accreditation application.")
            : Results.Ok(
                new SubmitResponse
                {
                    AccreditationReference = updated.ApplicationReference,
                    CaseManagementReference = updated.CaseManagementReference,
                }
            );
    }

    private static async Task<IResult> AddFile(
        string organisationId,
        string applicationId,
        FileUploadRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<FileUploadRequest> validator
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        if (application.SamplingPlan.Files.Count >= 10)
            return Results.UnprocessableEntity("Maximum of 10 files permitted per application.");

        var file = new AccreditationApplicationFile
        {
            FileId = request.FileId,
            Filename = request.Filename,
            ContentType = request.ContentType,
            UploadedByUserId = string.Empty, // TODO: populate from auth claims once auth PR lands
            ScanStatus = request.ScanStatus ?? FileScanStatus.Pending,
        };

        application.SamplingPlan.Files.Add(file);
        application.SamplingPlan.SectionStatus = SectionStatusService.ComputeSamplingPlan(
            application.SamplingPlan
        );
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to add file.")
            : Results.Created(string.Empty, file);
    }

    private static async Task<IResult> DeleteFile(
        string organisationId,
        string applicationId,
        string fileId,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        var removed = application.SamplingPlan.Files.RemoveAll(f => f.FileId == fileId);
        if (removed == 0)
            return Results.NotFound();

        application.SamplingPlan.SectionStatus = SectionStatusService.ComputeSamplingPlan(
            application.SamplingPlan
        );
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await persistence.UpdateAsync(application);
        return updated is null ? Results.Problem("Failed to delete file.") : Results.Ok();
    }

    private static async Task<IResult> Approve(
        string organisationId,
        string applicationId,
        IAccreditationApplicationPersistence persistence,
        IReExApiAdapter reExAdapter,
        CancellationToken cancellationToken
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        if (application.ApplicationStatus == ApplicationStatus.Approved)
            return Results.Ok(application);

        if (application.ApplicationStatus != ApplicationStatus.Sent)
            return Results.Conflict("Only submitted applications can be approved.");

        var approvedDto = new ApprovedAccreditationDto
        {
            ApplicationId = applicationId,
            OrganisationId = organisationId,
            MaterialType = application.MaterialType,
            Year = application.Year,
            SiteId = application.RegistrationId,
            ApplicationReference = application.ApplicationReference ?? string.Empty,
            Prns = application.Prns,
            BusinessPlan = application.BusinessPlan,
        };

        // Call adapter before persisting: if adapter fails, DB is unchanged and the caller can retry safely.
        var writeResult = await reExAdapter.WriteApprovedAccreditationAsync(
            approvedDto,
            cancellationToken
        );
        if (!writeResult.IsSuccess)
            return Results.Problem(
                statusCode: 502,
                detail: "Failed to write approved accreditation to ReEx."
            );

        // TODO: Write approved data to org document's accreditations array once
        // IOrganisationPersistence.UpsertAccreditationAsync is implemented (deferred from RA-101).

        application.ApplicationStatus = ApplicationStatus.Approved;
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to approve accreditation application.")
            : Results.Ok(updated);
    }

    private static async Task<IResult> Reject(
        string organisationId,
        string applicationId,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        if (application.ApplicationStatus == ApplicationStatus.Rejected)
            return Results.Ok(application);

        if (application.ApplicationStatus != ApplicationStatus.Sent)
            return Results.Conflict("Only submitted applications can be rejected.");

        application.ApplicationStatus = ApplicationStatus.Rejected;
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to reject accreditation application.")
            : Results.Ok(updated);
    }

    private static Task<IResult> InitiateUpload(
        string organisationId,
        string applicationId,
        InitiateUploadRequest request,
        ICdpUploaderService cdpUploaderService,
        IPendingUploadService pendingUploadService,
        IOptions<CdpUploaderConfig> cdpConfig,
        IOptions<AppConfig> appConfig,
        CancellationToken cancellationToken
    ) =>
        InitiateUploadInternal(
            organisationId,
            applicationId,
            request,
            cdpUploaderService,
            pendingUploadService,
            cdpConfig,
            appConfig,
            cancellationToken,
            cdpConfig.Value.SamplingPlanBucket
        );

    private static Task<IResult> InitiateBesEvidenceUpload(
        string organisationId,
        string applicationId,
        InitiateUploadRequest request,
        ICdpUploaderService cdpUploaderService,
        IPendingUploadService pendingUploadService,
        IOptions<CdpUploaderConfig> cdpConfig,
        IOptions<AppConfig> appConfig,
        CancellationToken cancellationToken
    ) =>
        InitiateUploadInternal(
            organisationId,
            applicationId,
            request,
            cdpUploaderService,
            pendingUploadService,
            cdpConfig,
            appConfig,
            cancellationToken,
            cdpConfig.Value.BesEvidenceBucket
        );

    private static async Task<IResult> InitiateUploadInternal(
        string organisationId,
        string applicationId,
        InitiateUploadRequest request,
        ICdpUploaderService cdpUploaderService,
        IPendingUploadService pendingUploadService,
        IOptions<CdpUploaderConfig> cdpConfig,
        IOptions<AppConfig> appConfig,
        CancellationToken cancellationToken,
        string bucketPrefix
    )
    {
        var fileUploadId = Guid.NewGuid().ToString();
        var baseUrl = appConfig.Value.BaseUrl.TrimEnd('/');
        var callbackUrl = $"{baseUrl}/api/v1/accreditation-applications/files/upload-completed";
        var statusUrl =
            $"{baseUrl}/api/v1/accreditation-applications/{organisationId}/{applicationId}/files/{fileUploadId}/status";

        var metadata = new Dictionary<string, string>(request.Metadata ?? [])
        {
            ["fileUploadId"] = fileUploadId,
        };

        var cdpRequest = new CdpInitiateRequest
        {
            Redirect = request.RedirectUrl,
            Callback = callbackUrl,
            S3Bucket = request.S3Bucket,
            S3Path = $"{bucketPrefix.Trim('/')}/{request.S3Path.TrimStart('/')}",
            MimeTypes = request.MimeTypes,
            MaxFileSize = request.MaxFileSize,
            Metadata = metadata,
        };

        var cdpResponse = await cdpUploaderService.InitiateAsync(cdpRequest, cancellationToken);
        pendingUploadService.Create(fileUploadId, cdpResponse.StatusUrl);

        return Results.Ok(
            new InitiateUploadResponse
            {
                FileUploadId = fileUploadId,
                UploadUrl = cdpResponse.UploadUrl,
                StatusUrl = statusUrl,
            }
        );
    }

    private static IResult UploadCompleted(
        CdpCallbackPayload payload,
        IPendingUploadService pendingUploadService
    )
    {
        var fileUploadId = payload.Metadata?.GetValueOrDefault("fileUploadId");
        if (string.IsNullOrWhiteSpace(fileUploadId))
            return Results.BadRequest("Missing fileUploadId in callback metadata.");

        var file = payload.Form?.File;
        if (file is null)
            return Results.BadRequest("Missing file in callback payload.");

        pendingUploadService.Complete(fileUploadId, file);
        return Results.Ok();
    }

    private static IResult GetUploadStatus(
        string fileUploadId,
        IPendingUploadService pendingUploadService
    )
    {
        var status = pendingUploadService.GetStatus(fileUploadId);
        return Results.Ok(status);
    }
}
