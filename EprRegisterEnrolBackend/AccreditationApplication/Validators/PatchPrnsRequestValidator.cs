using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class PatchPrnsRequestValidator : AbstractValidator<PatchPrnsRequest>
{
    public PatchPrnsRequestValidator()
    {
        When(r => r.PlannedTonnageBand.HasValue, () =>
        {
            RuleFor(r => r.PlannedTonnageBand)
                .IsInEnum()
                .WithMessage("PlannedTonnageBand must be a valid tonnage band.");
        });

        When(r => r.Authorisers != null, () =>
        {
            RuleForEach(r => r.Authorisers).ChildRules(authoriser =>
            {
                authoriser.RuleFor(a => a.FullName).NotEmpty().WithMessage("Authoriser full name is required.");
                authoriser.RuleFor(a => a.Email).NotEmpty().EmailAddress().WithMessage("Authoriser email must be valid.");
            });
        });
    }
}
