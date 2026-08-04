using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class PromoteOverseasSiteRequestValidator : AbstractValidator<PromoteOverseasSiteRequest>
{
    private static readonly System.Text.RegularExpressions.Regex BaselOecdRegex = new(
        @"^(?:[A-Za-z]\d{4}|[A-Za-z]{2}\d{3})$",
        System.Text.RegularExpressions.RegexOptions.Compiled
    );

    private static readonly System.Text.RegularExpressions.Regex EmailRegex = new(
        @"^[^\s@]+@[^\s@]+\.[^\s@]+$",
        System.Text.RegularExpressions.RegexOptions.Compiled
    );

    private static readonly string[] ValidOperationCodes =
    [
        "R1",
        "R2",
        "R3",
        "R4",
        "R5",
        "R6",
        "R7",
        "R8",
        "R9",
        "R10",
        "R11",
        "R12",
        "R13",
    ];

    public PromoteOverseasSiteRequestValidator()
    {
        RuleFor(r => r.SiteName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.AddressLine1).NotEmpty().MaximumLength(200);
        RuleFor(r => r.AddressLine2).MaximumLength(200);
        RuleFor(r => r.TownOrCity).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Country).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Coordinates).MaximumLength(50);
        RuleFor(r => r.ContactName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.ContactEmail)
            .NotEmpty()
            .MaximumLength(254)
            .Matches(EmailRegex)
            .WithMessage("ContactEmail must be a valid email address.");
        RuleFor(r => r.ContactPhone).MaximumLength(30);
        RuleFor(r => r.OperationCode)
            .NotEmpty()
            .Must(c => ValidOperationCodes.Contains(c))
            .WithMessage(
                $"OperationCode must be one of: {string.Join(", ", ValidOperationCodes)}."
            );
        RuleFor(r => r.Code1)
            .NotEmpty()
            .Matches(BaselOecdRegex)
            .WithMessage(
                "Code1 must be a valid Basel Convention or OECD code (e.g. A1181 or GC010)."
            );
        RuleFor(r => r.Code2)
            .Matches(BaselOecdRegex)
            .WithMessage("Code2 must be a valid Basel Convention or OECD code.")
            .When(r => !string.IsNullOrEmpty(r.Code2));
        RuleFor(r => r.Code3)
            .Matches(BaselOecdRegex)
            .WithMessage("Code3 must be a valid Basel Convention or OECD code.")
            .When(r => !string.IsNullOrEmpty(r.Code3));
        RuleFor(r => r.RepatriatedLoads).NotEmpty().MaximumLength(5000);
    }
}
