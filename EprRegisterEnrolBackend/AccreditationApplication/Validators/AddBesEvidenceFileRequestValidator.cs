using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class AddBesEvidenceFileRequestValidator : AbstractValidator<AddBesEvidenceFileRequest>
{
    public AddBesEvidenceFileRequestValidator()
    {
        RuleFor(r => r.FileUploadId).NotEmpty().MaximumLength(100);
    }
}
