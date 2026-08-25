using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Utils;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class AddOverseasSiteRequestValidator : AbstractValidator<AddOverseasSiteRequest>
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

    // RA-479: the frontend hint promises latitude/longitude to at least 4 decimal places
    // (~11m accuracy), so the backend enforces the same shape as a defence-in-depth check
    // rather than trusting the frontend to have validated it.
    private static readonly System.Text.RegularExpressions.Regex CoordinatesFormatRegex = new(
        @"^-?\d+\.\d{4,}\s*,\s*-?\d+\.\d{4,}$",
        System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100)
    );

    private static bool IsCoordinatesWithinRange(string coordinates)
    {
        var parts = coordinates.Split(',');
        var latitude = double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
        var longitude = double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
        return latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
    }

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

    public AddOverseasSiteRequestValidator()
    {
        RuleFor(r => r.SiteName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.AddressLine1).NotEmpty().MaximumLength(200);
        RuleFor(r => r.AddressLine2).MaximumLength(200);
        RuleFor(r => r.TownOrCity).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Country).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Coordinates).MaximumLength(50);
        RuleFor(r => r.Coordinates)
            .Must(c => CoordinatesFormatRegex.IsMatch(c!))
            .WithMessage(
                "Coordinates must be latitude and longitude to at least 4 decimal places, separated by a comma, e.g. 51.5034, -0.1275."
            )
            .When(r => !string.IsNullOrWhiteSpace(r.Coordinates));
        RuleFor(r => r.Coordinates)
            .Must(c => IsCoordinatesWithinRange(c!))
            .WithMessage(
                "Coordinates latitude must be between -90 and 90 and longitude must be between -180 and 180."
            )
            .When(r =>
                !string.IsNullOrWhiteSpace(r.Coordinates)
                && CoordinatesFormatRegex.IsMatch(r.Coordinates)
            );
        RuleFor(r => r.ContactName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.ContactEmail)
            .NotEmpty()
            .MaximumLength(254)
            .Matches(EmailRegex)
            .WithMessage("ContactEmail must be a valid email address.");
        RuleFor(r => r.ContactPhone).MaximumLength(30);
        RuleFor(r => r.OperationCodes).NotEmpty().WithMessage("OperationCodes must not be empty.");
        RuleForEach(r => r.OperationCodes)
            .Must(c => ValidOperationCodes.Contains(c))
            .WithMessage(
                $"OperationCodes must each be one of: {string.Join(", ", ValidOperationCodes)}."
            );
        RuleFor(r => r.OperationCodes)
            .Must(codes =>
                !(codes.Contains("R12") || codes.Contains("R13"))
                || codes.Any(c => c != "R12" && c != "R13")
            )
            .WithMessage(
                "R12 and R13 cannot be selected in isolation and must be accompanied by at least one other applicable R code."
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
