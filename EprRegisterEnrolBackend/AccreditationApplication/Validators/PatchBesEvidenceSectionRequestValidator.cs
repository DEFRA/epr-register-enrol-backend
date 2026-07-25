using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class PatchBesEvidenceSectionRequestValidator : AbstractValidator<PatchBesEvidenceSectionRequest>
{
    public PatchBesEvidenceSectionRequestValidator()
    {
        RuleFor(r => r.SectionStatus)
            .NotEqual(SectionStatus.Queried)
            .WithMessage("SectionStatus cannot be set to Queried directly.");
    }
}
