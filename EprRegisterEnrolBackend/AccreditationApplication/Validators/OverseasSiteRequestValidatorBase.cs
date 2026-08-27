using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Utils;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

// Shared by AddOverseasSiteRequestValidator and PromoteOverseasSiteRequestValidator,
// which validate identically-shaped requests (see IOverseasSiteRequestFields) that differ
// only in what the endpoint does with a matching site afterwards. Pulled out once the two
// validators' rule sets had drifted into a SonarCloud-flagged near-duplicate of each other.
public abstract class OverseasSiteRequestValidatorBase<T> : AbstractValidator<T>
    where T : IOverseasSiteRequestFields
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

    protected OverseasSiteRequestValidatorBase()
    {
        RuleFor(r => r.SiteName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.AddressLine1).NotEmpty().MaximumLength(200);
        RuleFor(r => r.AddressLine2).MaximumLength(200);
        RuleFor(r => r.TownOrCity).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Country).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Coordinates)
            .ValidCoordinates()
            .When(r => !string.IsNullOrWhiteSpace(r.Coordinates));
        RuleFor(r => r.ContactName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.ContactEmail)
            .NotEmpty()
            .MaximumLength(254)
            .Matches(EmailRegex)
            .WithMessage("ContactEmail must be a valid email address.");
        RuleFor(r => r.ContactPhone).MaximumLength(30);
        RuleFor(r => r.OperationCodes).NotEmpty().WithMessage("OperationCodes must not be empty.");
        RuleForEach(r => r.OperationCodes)
            .Must(c => RecyclingOperationCodes.AllCodes.Contains(c))
            .WithMessage(
                $"OperationCodes must each be one of: {string.Join(", ", RecyclingOperationCodes.AllCodes)}."
            );
        RuleFor(r => r.OperationCodes)
            .Must(RecyclingOperationCodes.HasMandatoryOrsCode)
            .WithMessage(
                $"OperationCodes must include at least one of: {string.Join(", ", RecyclingOperationCodes.MaterialCodes)}."
            );
        RuleFor(r => r.Code1)
            .NotEmpty()
            .Must(c => BaselOecdCodes.ApprovedCodes.Contains(c))
            .WithMessage(
                "Code1 must be a valid Basel Convention or OECD code (e.g. A1181 or GC030)."
            );
        RuleFor(r => r.Code2)
            .Must(c => BaselOecdCodes.ApprovedCodes.Contains(c!))
            .WithMessage("Code2 must be a valid Basel Convention or OECD code.")
            .When(r => !string.IsNullOrEmpty(r.Code2));
        RuleFor(r => r.Code3)
            .Must(c => BaselOecdCodes.ApprovedCodes.Contains(c!))
            .WithMessage("Code3 must be a valid Basel Convention or OECD code.")
            .When(r => !string.IsNullOrEmpty(r.Code3));
        RuleFor(r => r)
            .Must(r => !BaselOecdCodes.HasDuplicateCode(r.Code1, r.Code2, r.Code3))
            .WithMessage("Code1, Code2 and Code3 must not contain duplicate codes.")
            .WithName("Code2");
        RuleFor(r => r.RepatriatedLoads).NotEmpty().MaximumLength(5000);
    }
}