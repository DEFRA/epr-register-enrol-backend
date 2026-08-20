using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

// RA-448 / AC6: validation must happen before the generator is ever called, so a
// bad request never consumes a sequence value from any counter. Nation/OrgId/Year
// are only required when actually generating a fresh number - an idempotent
// re-call or a pure-string-transform accreditation regenerate never needs them,
// so this validator only checks shape (Nation is a recognised enum value, OrgId
// fits the format's fixed 6-digit segment, Year is a plausible 4-digit year, all
// only if present) rather than presence; the endpoint itself decides whether it
// has what it needs for the branch it's about to take.
public class GenerateOrUpdateRegulatoryNumberRequestValidator
    : AbstractValidator<GenerateOrUpdateRegulatoryNumberRequest>
{
    public GenerateOrUpdateRegulatoryNumberRequestValidator()
    {
        RuleFor(r => r.Nation)
            .Must(n => Enum.TryParse<Nation>(n, ignoreCase: true, out _))
            .When(r => r.Nation is not null)
            .WithMessage("Nation is not a recognised value.");

        // Bounded to the format's fixed 6-digit OrgID segment, not just positive:
        // {orgId:D6} pads to AT LEAST 6 digits but never truncates, so an
        // unbounded OrgId (e.g. 1234567) would silently widen the segment and
        // break the fixed-width public-register format instead of failing loudly.
        RuleFor(r => r.OrgId)
            .InclusiveBetween(1, 999999)
            .When(r => r.OrgId is not null)
            .WithMessage("OrgId must be between 1 and 999999 (fits the 6-digit segment).");

        // Bounded rather than just GreaterThan(0): the number format only carries
        // the last two digits, so an implausible year (e.g. 2126) would silently
        // collide with a real one (26) via truncation instead of failing loudly.
        RuleFor(r => r.Year)
            .InclusiveBetween(2000, 2099)
            .When(r => r.Year is not null)
            .WithMessage("Year must be between 2000 and 2099.");
    }
}
