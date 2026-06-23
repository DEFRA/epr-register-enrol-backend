using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.Organisation.Services;
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
        group.MapPost("{organisationId}/{applicationId}/submit", Submit);
        group.MapPost("{organisationId}/{applicationId}/files", AddFile);
        group.MapDelete("{organisationId}/{applicationId}/files/{fileId}", DeleteFile);
        group.MapPost("{organisationId}/{applicationId}/approve", Approve);
        group.MapPost("{organisationId}/{applicationId}/reject", Reject);
        group.MapPost("{organisationId}/{applicationId}/files/initiate", InitiateUpload);
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
        IOrganisationPersistence organisationPersistence,
        IValidator<SeedRequest> validator
    )
    {
        if (!Enum.TryParse<MaterialType>(materialType, out var materialTypeEnum))
            return Results.BadRequest("Invalid material type.");

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var priorYearData = await reExAdapter.GetAccreditationAsync(
            organisationId,
            materialTypeEnum,
            request.Year - 1
        );

        string? organisationName = null;
        string? siteAddress = null;
        string? registrationReference = null;
        List<string>? overseasSiteIds = null;
        var isExporter = false;
        if (int.TryParse(organisationId, out var orgIdInt))
        {
            var org = await organisationPersistence.GetByOrgIdAsync(orgIdInt);
            organisationName = org?.CompanyDetails?.Name;
            registrationReference = org?.CompanyDetails?.RegistrationNumber;
            var registration = org?.Registrations?.FirstOrDefault(r =>
                r.Id.ToString() == registrationId
            );
            overseasSiteIds = registration?.OverseasSites;
            isExporter =
                registration?.WasteProcessingType?.Equals(
                    "exporter",
                    StringComparison.OrdinalIgnoreCase
                ) == true;
            siteAddress = registration?.SiteAddress is { } addr
                ? $"{addr.Line1}, {addr.Town}, {addr.Postcode}"
                : null;
        }

        var application = new AccreditationApplicationModel
        {
            OrganisationId = organisationId,
            OrganisationName = organisationName,
            Year = request.Year,
            RegistrationId = registrationId,
            IsExporter = isExporter,
            SiteAddress = siteAddress,
            RegistrationReference = registrationReference,
            MaterialType = materialTypeEnum,
            ApplicationStatus = ApplicationStatus.Saved,
            SourceReExAccreditationId = priorYearData?.AccreditationId,
            SourceYear = priorYearData != null ? request.Year - 1 : null,
            OverseasSites = isExporter
                ? new AccreditationApplicationOverseasSites
                {
                    Sites = BuildStubOverseasSites(overseasSiteIds),
                }
                : null,
        };

        if (priorYearData != null)
        {
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
                    BusinessCollectionsPercent = priorYearData
                        .BusinessPlan
                        .BusinessCollectionsPercent,
                    CommunicationsPercent = priorYearData.BusinessPlan.CommunicationsPercent,
                    NewMarketsPercent = priorYearData.BusinessPlan.NewMarketsPercent,
                    NewUsesPercent = priorYearData.BusinessPlan.NewUsesPercent,
                    SectionStatus = SectionStatus.NotStarted,
                };
            }
        }

        var existing = (await persistence.GetByOrganisationAsync(organisationId)).FirstOrDefault(
            a =>
                a.RegistrationId == registrationId
                && a.MaterialType == materialTypeEnum
                && a.Year == request.Year
        );
        if (existing is not null)
            return Results.Ok(existing);

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
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        return application is null ? Results.NotFound() : Results.Ok(application);
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
                new SubmitResponse { AccreditationReference = application.ApplicationReference }
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
        application.ApplicationReference = await caseWorkingAdapter.SubmitApplicationAsync(
            application,
            cancellationToken
        );

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to submit accreditation application.")
            : Results.Ok(
                new SubmitResponse { AccreditationReference = updated.ApplicationReference }
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

        // TODO: define contract with ReEx and extend Dto as required.
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
        await reExAdapter.WriteApprovedAccreditationAsync(approvedDto, cancellationToken);

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

    private static async Task<IResult> InitiateUpload(
        string organisationId,
        string applicationId,
        InitiateUploadRequest request,
        ICdpUploaderService cdpUploaderService,
        IPendingUploadService pendingUploadService,
        IOptions<CdpUploaderConfig> cdpConfig,
        IOptions<AppConfig> appConfig,
        CancellationToken cancellationToken
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
            S3Bucket = cdpConfig.Value.SamplingPlanBucket,
            S3Path = request.S3Path,
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

    // TODO: replace with real overseas site lookup from ReEx once contract is defined.
    private static List<OverseasSiteModel> BuildStubOverseasSites(List<string>? registrationSiteIds)
    {
        if (registrationSiteIds is null || registrationSiteIds.Count == 0)
            return [];

        return registrationSiteIds
            .Select(
                (id, i) =>
                    new OverseasSiteModel
                    {
                        SiteId = int.TryParse(id, out var parsed) ? parsed : 900001 + i,
                        SiteName = $"Overseas Site {i + 1}",
                        SiteAddress = $"Address {id}",
                        Country = i switch
                        {
                            0 => "Germany",
                            1 => "France",
                            _ => "Netherlands",
                        },
                        IsEu = true,
                        IsOecd = true,
                    }
            )
            .ToList();
    }
}
