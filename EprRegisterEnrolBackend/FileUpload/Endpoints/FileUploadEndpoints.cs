using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.FileUpload.Config;
using EprRegisterEnrolBackend.FileUpload.Models;
using EprRegisterEnrolBackend.FileUpload.Services;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.FileUpload.Endpoints;

public static class FileUploadEndpoints
{
    public static void UseFileUploadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/v1/file-uploads");

        group.MapPost(string.Empty, Create);
        group.MapGet(string.Empty, GetList);
        group.MapGet("{fileUploadId}", GetById);
        group.MapGet("{fileUploadId}/download", Download);
        group.MapPost("initiate", InitiateFileUpload);
        group.MapPost("upload-completed", FileUploadCompleted);
        group.MapGet("{fileUploadId}/status", GetFileUploadStatus);
    }

    private static async Task<IResult> Create(
        CreateFileUploadRequest request,
        IFileUploadPersistence persistence,
        IValidator<CreateFileUploadRequest> validator
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var fileUpload = new FileUploadModel
        {
            OrganisationId = request.OrganisationId,
            Material = request.Material,
            YearOfAccreditation = request.YearOfAccreditation,
            FileId = request.FileId,
            Filename = request.Filename,
            ContentType = request.ContentType,
            S3Key = request.S3Key,
            UploadedByUserId = request.UploadedByUserId,
            ScanStatus = request.ScanStatus,
        };

        var created = await persistence.CreateAsync(fileUpload);
        if (created is null)
            return Results.Problem("Failed to create file upload record.");

        return Results.Created($"/api/v1/file-uploads/{created.FileUploadId}", created);
    }

    private static async Task<IResult> GetList(
        string? organisationId,
        string? material,
        int? year,
        IFileUploadPersistence persistence
    )
    {
        if (string.IsNullOrWhiteSpace(organisationId))
            return Results.BadRequest("organisationId query parameter is required.");

        if (!string.IsNullOrWhiteSpace(material))
        {
            if (!Enum.TryParse<MaterialType>(material, out var materialType))
                return Results.BadRequest($"Invalid material value: '{material}'.");

            if (year.HasValue)
            {
                var filtered = await persistence.GetByOrganisationMaterialAndYearAsync(
                    organisationId,
                    materialType,
                    year.Value
                );
                return Results.Ok(filtered);
            }
        }

        var files = await persistence.GetByOrganisationAsync(organisationId);
        return Results.Ok(files);
    }

    private static async Task<IResult> GetById(
        string fileUploadId,
        IFileUploadPersistence persistence
    )
    {
        var fileUpload = await persistence.GetByIdAsync(fileUploadId);
        return fileUpload is null ? Results.NotFound() : Results.Ok(fileUpload);
    }

    private static async Task<IResult> Download(
        string fileUploadId,
        IFileUploadPersistence persistence,
        IS3Service s3Service,
        IOptions<CdpUploaderConfig> cdpConfig,
        CancellationToken cancellationToken
    )
    {
        var fileUpload = await persistence.GetByIdAsync(fileUploadId);
        if (fileUpload is null)
            return Results.NotFound();

        if (fileUpload.ScanStatus != FileScanStatus.Clean)
            return Results.UnprocessableEntity(
                "File is not available for download: scan status is not clean."
            );

        var presignedUrl = await s3Service.GeneratePresignedDownloadUrlAsync(
            cdpConfig.Value.GenericFilesBucket,
            fileUpload.S3Key,
            fileUpload.Filename,
            cancellationToken
        );

        return Results.Ok(new { presignedUrl });
    }

    private static async Task<IResult> InitiateFileUpload(
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
        var callbackUrl = $"{baseUrl}/api/v1/file-uploads/upload-completed";
        var statusUrl = $"{baseUrl}/api/v1/file-uploads/{fileUploadId}/status";

        var metadata = new Dictionary<string, string>(request.Metadata ?? [])
        {
            ["fileUploadId"] = fileUploadId,
        };

        var cdpRequest = new CdpInitiateRequest
        {
            Redirect = request.RedirectUrl,
            Callback = callbackUrl,
            S3Bucket = cdpConfig.Value.GenericFilesBucket,
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

    private static IResult FileUploadCompleted(
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

    private static IResult GetFileUploadStatus(
        string fileUploadId,
        IPendingUploadService pendingUploadService
    )
    {
        var status = pendingUploadService.GetStatus(fileUploadId);
        return Results.Ok(status);
    }
}
