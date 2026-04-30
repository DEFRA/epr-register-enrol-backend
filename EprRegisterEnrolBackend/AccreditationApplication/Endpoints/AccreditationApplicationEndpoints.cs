using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Endpoints;

public static class AccreditationApplicationEndpoints
{
    public static void UseAccreditationApplicationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/v1/accreditation-applications");

        group.MapPost("{organisationId}/seed", Seed);
        group.MapGet("{organisationId}", GetList);
        group.MapGet("{organisationId}/{applicationId}", GetById);
        group.MapPatch("{organisationId}/{applicationId}/prns", PatchPrns);
        group.MapPatch("{organisationId}/{applicationId}/business-plan", PatchBusinessPlan);
        group.MapPatch("{organisationId}/{applicationId}/sampling-plan", PatchSamplingPlan);
        group.MapPost("{organisationId}/{applicationId}/submit", Submit);
        group.MapPost("{organisationId}/{applicationId}/files", AddFile);
        group.MapDelete("{organisationId}/{applicationId}/files/{fileId}", DeleteFile);
        group.MapPost("{organisationId}/{applicationId}/approve", Approve);
        group.MapPost("{organisationId}/{applicationId}/reject", Reject);
    }

    private static async Task<IResult> Seed(
        string organisationId,
        SeedRequest request,
        IAccreditationApplicationPersistence persistence,
        IReExApiAdapter reExAdapter,
        IValidator<SeedRequest> validator)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var priorYearData = await reExAdapter.GetAccreditationAsync(organisationId, request.MaterialType, request.Year - 1);

        var application = new AccreditationApplicationModel
        {
            OrganisationId = organisationId,
            Year = request.Year,
            SiteId = request.SiteId ?? priorYearData?.SiteId,
            MaterialType = request.MaterialType,
            ApplicationStatus = ApplicationStatus.Saved,
            SourceReExAccreditationId = priorYearData?.AccreditationId,
            SourceYear = priorYearData != null ? request.Year - 1 : null
        };

        if (priorYearData != null)
        {
            if (priorYearData.Prns != null)
            {
                application.Prns = new AccreditationApplicationPrns
                {
                    PlannedTonnageBand = priorYearData.Prns.PlannedTonnageBand,
                    Authorisers = priorYearData.Prns.Authorisers,
                    SectionStatus = SectionStatus.NotStarted
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
                    SectionStatus = SectionStatus.NotStarted
                };
            }
        }

        application.DateLastEdited = application.CreatedAt;

        var created = await persistence.CreateAsync(application);
        if (created is null)
            return Results.Problem("Failed to create accreditation application.");

        return Results.Created($"/api/v1/accreditation-applications/{organisationId}/{created.Id}", created);
    }

    private static async Task<IResult> GetList(
        string organisationId,
        IAccreditationApplicationPersistence persistence)
    {
        var applications = await persistence.GetByOrganisationAsync(organisationId);
        return Results.Ok(applications);
    }

    private static async Task<IResult> GetById(
        string organisationId,
        string applicationId,
        IAccreditationApplicationPersistence persistence)
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        return application is null ? Results.NotFound() : Results.Ok(application);
    }

    private static async Task<IResult> PatchPrns(
        string organisationId,
        string applicationId,
        PatchPrnsRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<PatchPrnsRequest> validator)
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
        return updated is null ? Results.Problem("Failed to update PRNs section.") : Results.Ok(updated);
    }

    private static async Task<IResult> PatchBusinessPlan(
        string organisationId,
        string applicationId,
        PatchBusinessPlanRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<PatchBusinessPlanRequest> validator)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.UnprocessableEntity(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        var bp = application.BusinessPlan;
        if (request.NewInfrastructurePercent.HasValue) bp.NewInfrastructurePercent = request.NewInfrastructurePercent;
        if (request.PriceSupportPercent.HasValue) bp.PriceSupportPercent = request.PriceSupportPercent;
        if (request.BusinessCollectionsPercent.HasValue) bp.BusinessCollectionsPercent = request.BusinessCollectionsPercent;
        if (request.CommunicationsPercent.HasValue) bp.CommunicationsPercent = request.CommunicationsPercent;
        if (request.NewMarketsPercent.HasValue) bp.NewMarketsPercent = request.NewMarketsPercent;
        if (request.NewUsesPercent.HasValue) bp.NewUsesPercent = request.NewUsesPercent;

        if (request.NewInfrastructureDetail != null) bp.NewInfrastructureDetail = request.NewInfrastructureDetail;
        if (request.PriceSupportDetail != null) bp.PriceSupportDetail = request.PriceSupportDetail;
        if (request.BusinessCollectionsDetail != null) bp.BusinessCollectionsDetail = request.BusinessCollectionsDetail;
        if (request.CommunicationsDetail != null) bp.CommunicationsDetail = request.CommunicationsDetail;
        if (request.NewMarketsDetail != null) bp.NewMarketsDetail = request.NewMarketsDetail;
        if (request.NewUsesDetail != null) bp.NewUsesDetail = request.NewUsesDetail;

        bp.SectionStatus = SectionStatusService.ComputeBusinessPlan(bp);
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null ? Results.Problem("Failed to update business plan section.") : Results.Ok(updated);
    }

    private static async Task<IResult> PatchSamplingPlan(
        string organisationId,
        string applicationId,
        PatchSamplingPlanRequest request,
        IAccreditationApplicationPersistence persistence)
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        if (request.Files != null)
            application.SamplingPlan.Files = request.Files;

        application.SamplingPlan.SectionStatus = SectionStatusService.ComputeSamplingPlan(application.SamplingPlan);
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null ? Results.Problem("Failed to update sampling plan section.") : Results.Ok(updated);
    }

    private static async Task<IResult> Submit(
        string organisationId,
        string applicationId,
        SubmitRequest request,
        IAccreditationApplicationPersistence persistence,
        ICaseWorkingApiAdapter caseWorkingAdapter,
        IApplicationReferenceService referenceService,
        IValidator<SubmitRequest> validator)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        if (application.Prns.SectionStatus != SectionStatus.Completed ||
            application.BusinessPlan.SectionStatus != SectionStatus.Completed ||
            application.SamplingPlan.SectionStatus != SectionStatus.Completed)
        {
            return Results.BadRequest("All sections must be completed before submission.");
        }

        application.ApplicationReference = referenceService.Generate(application.Year);
        application.ApplicationStatus = ApplicationStatus.Sent;
        application.DateSent = DateTime.UtcNow;
        application.DateLastEdited = DateTime.UtcNow;
        application.SubmittedBy = new SubmittedByModel
        {
            FullName = request.FullName,
            JobTitle = request.JobTitle,
            Email = request.Email
        };

        var updated = await persistence.UpdateAsync(application);
        if (updated is null)
            return Results.Problem("Failed to submit accreditation application.");

        await caseWorkingAdapter.SubmitApplicationAsync(updated);

        return Results.Ok(updated);
    }

    private static async Task<IResult> AddFile(
        string organisationId,
        string applicationId,
        FileUploadRequest request,
        IAccreditationApplicationPersistence persistence)
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        var file = new AccreditationApplicationFile
        {
            FileId = request.FileId,
            Filename = request.Filename,
            ContentType = request.ContentType,
            UploadedByUserId = request.UploadedByUserId,
            ScanStatus = FileScanStatus.Pending
        };

        application.SamplingPlan.Files.Add(file);
        application.SamplingPlan.SectionStatus = SectionStatusService.ComputeSamplingPlan(application.SamplingPlan);
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null ? Results.Problem("Failed to add file.") : Results.Created(string.Empty, file);
    }

    private static async Task<IResult> DeleteFile(
        string organisationId,
        string applicationId,
        string fileId,
        IAccreditationApplicationPersistence persistence)
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        var removed = application.SamplingPlan.Files.RemoveAll(f => f.FileId == fileId);
        if (removed == 0)
            return Results.NotFound();

        application.SamplingPlan.SectionStatus = SectionStatusService.ComputeSamplingPlan(application.SamplingPlan);
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await persistence.UpdateAsync(application);
        return updated is null ? Results.Problem("Failed to delete file.") : Results.Ok();
    }

    private static async Task<IResult> Approve(
        string organisationId,
        string applicationId,
        IAccreditationApplicationPersistence persistence,
        IReExApiAdapter reExAdapter)
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        application.ApplicationStatus = ApplicationStatus.Approved;
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await persistence.UpdateAsync(application);
        if (updated is null)
            return Results.Problem("Failed to approve accreditation application.");

        // TODO: Write approved data to org document's accreditations array once
        // IOrganisationPersistence.UpsertAccreditationAsync is implemented (deferred from RA-101).

        var approvedDto = new ApprovedAccreditationDto
        {
            ApplicationId = applicationId,
            OrganisationId = organisationId,
            MaterialType = application.MaterialType,
            Year = application.Year,
            SiteId = application.SiteId,
            ApplicationReference = application.ApplicationReference ?? string.Empty,
            Prns = application.Prns,
            BusinessPlan = application.BusinessPlan
        };

        await reExAdapter.WriteApprovedAccreditationAsync(approvedDto);

        return Results.Ok(updated);
    }

    private static async Task<IResult> Reject(
        string organisationId,
        string applicationId,
        IAccreditationApplicationPersistence persistence)
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        application.ApplicationStatus = ApplicationStatus.Rejected;
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await persistence.UpdateAsync(application);
        return updated is null ? Results.Problem("Failed to reject accreditation application.") : Results.Ok(updated);
    }

}
