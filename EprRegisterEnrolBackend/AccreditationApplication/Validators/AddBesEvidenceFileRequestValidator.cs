using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class AddBesEvidenceFileRequestValidator : AbstractValidator<AddBesEvidenceFileRequest>
{
    public AddBesEvidenceFileRequestValidator()
    {
        RuleFor(r => r.FileId).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Filename)
            .NotEmpty()
            .MaximumLength(255)
            .Matches(@"^[^\x00-\x1f<>:""/\\|?*]+$")
            .WithMessage("Filename contains invalid characters.");
        RuleFor(r => r.S3Key).NotEmpty().MaximumLength(1024);
    }
}
