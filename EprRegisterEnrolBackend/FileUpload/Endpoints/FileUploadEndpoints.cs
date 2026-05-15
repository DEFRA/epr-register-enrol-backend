using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.FileUpload.Models;
using EprRegisterEnrolBackend.FileUpload.Services;
using FluentValidation;

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
    }

    private static async Task<IResult> Create(
        CreateFileUploadRequest request,
        IFileUploadPersistence persistence,
        IValidator<CreateFileUploadRequest> validator)
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
            ScanStatus = request.ScanStatus
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
        IFileUploadPersistence persistence)
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
                    organisationId, materialType, year.Value);
                return Results.Ok(filtered);
            }
        }

        var files = await persistence.GetByOrganisationAsync(organisationId);
        return Results.Ok(files);
    }

    private static async Task<IResult> GetById(
        string fileUploadId,
        IFileUploadPersistence persistence)
    {
        var fileUpload = await persistence.GetByIdAsync(fileUploadId);
        return fileUpload is null ? Results.NotFound() : Results.Ok(fileUpload);
    }

    private static async Task<IResult> Download(
        string fileUploadId,
        IFileUploadPersistence persistence)
    {
        var fileUpload = await persistence.GetByIdAsync(fileUploadId);
        if (fileUpload is null)
            return Results.NotFound();

        if (fileUpload.ScanStatus != FileScanStatus.Clean)
            return Results.UnprocessableEntity("File is not available for download: scan status is not clean.");

        // TODO: Generate a presigned S3 URL using AWSSDK.S3 and return a redirect.
        // For now, return the file metadata so the caller can construct a download URL.
        return Results.Ok(new
        {
            fileUpload.FileUploadId,
            fileUpload.Filename,
            fileUpload.ContentType,
            fileUpload.S3Key,
            Message = "Presigned URL generation not yet implemented. Use S3Key to retrieve from S3."
        });
    }
}
