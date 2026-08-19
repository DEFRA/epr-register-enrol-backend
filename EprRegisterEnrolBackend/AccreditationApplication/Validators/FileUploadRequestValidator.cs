using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class FileUploadRequestValidator : AbstractValidator<FileUploadRequest>
{
    // Content type on the real uploaded file is checked against this list server-side
    // (in AccreditationApplicationEndpoints.AddFile) once resolved from PendingUploadService —
    // exposed here so both places share one list.
    public static readonly string[] PermittedContentTypes =
    [
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "text/csv",
        "image/jpeg",
        "image/png",
        "image/tiff",
        "application/vnd.ms-outlook",
    ];

    public FileUploadRequestValidator()
    {
        RuleFor(r => r.FileUploadId).NotEmpty().MaximumLength(100);

        When(
            r => r.DocumentType.HasValue,
            () =>
            {
                RuleFor(r => r.DocumentType)
                    .IsInEnum()
                    .WithMessage("DocumentType must be a valid document type.");
            }
        );
    }
}
