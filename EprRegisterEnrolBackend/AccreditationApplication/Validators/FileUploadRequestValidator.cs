using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class FileUploadRequestValidator : AbstractValidator<FileUploadRequest>
{
    private static readonly string[] PermittedContentTypes =
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
        "application/vnd.ms-outlook"
    ];

    public FileUploadRequestValidator()
    {
        RuleFor(r => r.FileId).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Filename)
            .NotEmpty()
            .MaximumLength(255)
            .Matches(@"^[^\x00-\x1f<>:""/\\|?*]+$")
            .WithMessage("Filename contains invalid characters.");
        RuleFor(r => r.ContentType)
            .NotEmpty()
            .Must(ct => PermittedContentTypes.Contains(ct))
            .WithMessage("Content type is not permitted.");
    }
}
