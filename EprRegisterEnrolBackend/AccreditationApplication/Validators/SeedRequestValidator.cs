using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class SeedRequestValidator : AbstractValidator<SeedRequest>
{
    public SeedRequestValidator()
    {
        RuleFor(r => r.Year)
            .GreaterThanOrEqualTo(2024)
            .WithMessage("Year must be 2024 or later.");
    }
}
