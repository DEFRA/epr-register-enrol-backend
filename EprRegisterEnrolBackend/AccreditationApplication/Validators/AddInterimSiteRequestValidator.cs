using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class AddInterimSiteRequestValidator : AbstractValidator<AddInterimSiteRequest>
{
    // S6444: ContactEmail arrives straight off a client request body, so the match is given
    // an explicit timeout rather than being left to run unbounded on the request thread.
    // The trailing label also excludes '.' (unlike the two classes before it): with
    // [^\s@]+\.[^\s@]+ every dot in the domain is a candidate split point for the literal,
    // which makes a failing match quadratic in the input length. Pinning the final label to
    // the last dot removes the ambiguity. Only behaviour change: a trailing-dot address
    // ("a@b.com.") no longer validates, and that was never a valid address.
    private static readonly System.Text.RegularExpressions.Regex EmailRegex = new(
        @"^[^\s@]+@[^\s@]+\.[^\s@.]+$",
        System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100)
    );

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
            .Matches(EmailRegex)
            .WithMessage("ContactEmail must be a valid email address.");
        RuleFor(r => r.ContactPhone)
            .NotEmpty()
            .MaximumLength(30)
            .Matches(PhoneRegex)
            .WithMessage("ContactPhone must be a valid phone number.");
    }
}
