using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

// RA-448 / AC6: validation must happen before the generator is ever called, so a
// bad request never consumes a sequence value from any counter. Nation/OrgId are
// only required when actually generating a fresh number - an idempotent re-call
// or a pure-string-transform accreditation regenerate never needs them, so this
// validator only checks shape (Nation is a recognised enum value if present,
// OrgId is positive if present) rather than presence; the endpoint itself decides
// whether it has what it needs for the branch it's about to take.
public class GenerateOrUpdateRegulatoryNumberRequestValidator
    : AbstractValidator<GenerateOrUpdateRegulatoryNumberRequest>
{
    public GenerateOrUpdateRegulatoryNumberRequestValidator()
    {
        RuleFor(r => r.Nation)
            .Must(n => Enum.TryParse<Nation>(n, ignoreCase: true, out _))
            .When(r => r.Nation is not null)
            .WithMessage("Nation is not a recognised value.");

        RuleFor(r => r.OrgId)
            .GreaterThan(0)
            .When(r => r.OrgId is not null)
            .WithMessage("OrgId must be a positive number.");
    }
}
