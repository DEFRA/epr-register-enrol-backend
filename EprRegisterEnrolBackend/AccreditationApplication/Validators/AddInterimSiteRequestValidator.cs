using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Utils;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class AddInterimSiteRequestValidator : AbstractValidator<AddInterimSiteRequest>
{
    private static readonly System.Text.RegularExpressions.Regex PhoneRegex = new(
        @"^\+?[0-9()\-\s]{7,20}$",
        System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100)
    );

    public AddInterimSiteRequestValidator()
    {
        RuleFor(r => r.Country).NotEmpty().MaximumLength(100);
        RuleFor(r => r.SiteName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.AddressLine1).NotEmpty().MaximumLength(200);
        RuleFor(r => r.AddressLine2).MaximumLength(200);
        RuleFor(r => r.TownOrCity).NotEmpty().MaximumLength(100);
        RuleFor(r => r.StateOrRegion).MaximumLength(100);
        RuleFor(r => r.Postcode).MaximumLength(20);
        RuleFor(r => r.ContactName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.ContactEmail)
            .NotEmpty()
            .MaximumLength(254)
            .Matches(OverseasSiteEmail.Pattern)
            .WithMessage("ContactEmail must be a valid email address.");
        RuleFor(r => r.ContactPhone)
            .NotEmpty()
            .MaximumLength(30)
            .Matches(PhoneRegex)
            .WithMessage("ContactPhone must be a valid phone number.");
        RuleFor(r => r.OperationCodes).NotEmpty().WithMessage("OperationCodes must not be empty.");
        RuleForEach(r => r.OperationCodes)
            .Must(c => RecyclingOperationCodes.AllCodes.Contains(c))
            .WithMessage(
                $"OperationCodes must each be one of: {string.Join(", ", RecyclingOperationCodes.AllCodes)}."
            );
        RuleFor(r => r.OperationCodes)
            .Must(RecyclingOperationCodes.HasMandatoryInterimCode)
            .WithMessage(
                $"OperationCodes must include at least one of: {string.Join(", ", RecyclingOperationCodes.InterimCodes)}."
            );
    }
}
