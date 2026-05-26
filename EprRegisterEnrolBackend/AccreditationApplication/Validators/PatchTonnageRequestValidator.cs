using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class PatchTonnageRequestValidator : AbstractValidator<PatchTonnageRequest>
{
    public PatchTonnageRequestValidator()
    {
        RuleFor(r => r.PlannedTonnageBand)
            .NotNull().WithMessage("PlannedTonnageBand is required.")
            .IsInEnum().WithMessage("PlannedTonnageBand must be a valid tonnage band.");
    }
}
