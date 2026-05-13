using EprRegisterEnrolBackend.FileUpload.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.FileUpload.Validators;

public class CreateFileUploadRequestValidator : AbstractValidator<CreateFileUploadRequest>
{
    private static readonly string[] PermittedContentTypes =
    [
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "image/jpeg",
        "image/png"
    ];

    private static readonly int MinYear = DateTime.UtcNow.Year - 2;
    private static readonly int MaxYear = DateTime.UtcNow.Year;

    public CreateFileUploadRequestValidator()
    {
        RuleFor(r => r.OrganisationId).NotEmpty().MaximumLength(100);

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

        RuleFor(r => r.S3Key).NotEmpty().MaximumLength(1024);

        RuleFor(r => r.YearOfAccreditation)
            .InclusiveBetween(MinYear, MaxYear)
            .WithMessage($"Year of accreditation must be between {MinYear} and {MaxYear}.");
    }
}
