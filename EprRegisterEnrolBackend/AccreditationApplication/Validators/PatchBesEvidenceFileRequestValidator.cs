using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class PatchBesEvidenceFileRequestValidator : AbstractValidator<PatchBesEvidenceFileRequest>
{
    public PatchBesEvidenceFileRequestValidator()
    {
        RuleFor(r => r.BesEvidenceValidFromDate)
            .Must(BeAParsableDate!)
            .When(r => r.BesEvidenceValidFromDate is not null)
            .WithMessage("BesEvidenceValidFromDate must be a valid ISO 8601 date.");

        RuleFor(r => r.BesEvidenceExpiryDate)
            .Must(BeAParsableDate!)
            .When(r => r.BesEvidenceExpiryDate is not null)
            .WithMessage("BesEvidenceExpiryDate must be a valid ISO 8601 date.");

        // Only checkable when both dates are supplied together and both parse; a single date is
        // validated on its own above (the stored counterpart isn't visible to the validator).
        RuleFor(r => r)
            .Must(r => Parse(r.BesEvidenceExpiryDate) >= Parse(r.BesEvidenceValidFromDate))
            .When(r => Parse(r.BesEvidenceValidFromDate) is not null && Parse(r.BesEvidenceExpiryDate) is not null)
            .WithName(nameof(PatchBesEvidenceFileRequest.BesEvidenceExpiryDate))
            .WithMessage("BesEvidenceExpiryDate cannot be before BesEvidenceValidFromDate.");
    }

    private static bool BeAParsableDate(string value) => Parse(value) is not null;

    private static DateTimeOffset? Parse(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed
        )
            ? parsed
            : null;
}
