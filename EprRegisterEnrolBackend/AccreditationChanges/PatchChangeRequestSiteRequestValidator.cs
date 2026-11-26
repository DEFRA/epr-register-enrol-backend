using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using EprRegisterEnrolBackend.Utils;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationChanges;

// RA-492: format rules for the values an operator supplies for a registered ORS - the same rules
// the ORS journey applies (OverseasSiteRequestValidatorBase). Whether a field may be supplied at
// all (it is locked once the ORS holds a value) needs the ORS itself, so the endpoint checks that.
public class PatchChangeRequestSiteRequestValidator
    : AbstractValidator<PatchChangeRequestSiteRequest>
{
    public PatchChangeRequestSiteRequestValidator()
    {
        RuleFor(r => r.Country)
            .NotEmpty()
            .MaximumLength(100)
            .When(r => r.Country is not null)
            .OverridePropertyName(ChangeRequestRules.Country);
        RuleFor(r => r.Coordinates)
            .NotEmpty()
            .When(r => r.Coordinates is not null)
            .OverridePropertyName(ChangeRequestRules.Coordinates);
        RuleFor(r => r.Coordinates)
            .ValidCoordinates()
            .When(r => !string.IsNullOrWhiteSpace(r.Coordinates))
            .OverridePropertyName(ChangeRequestRules.Coordinates);
        RuleFor(r => r.OperationCodes)
            .NotEmpty()
            .WithMessage("OperationCodes must not be empty.")
            .Must(codes => codes!.All(RecyclingOperationCodes.AllCodes.Contains))
            .WithMessage(
                $"OperationCodes must each be one of: {string.Join(", ", RecyclingOperationCodes.AllCodes)}."
            )
            .Must(codes => RecyclingOperationCodes.HasMandatoryOrsCode(codes!))
            .WithMessage(
                $"OperationCodes must include at least one of: {string.Join(", ", RecyclingOperationCodes.MaterialCodes)}."
            )
            .When(r => r.OperationCodes is not null)
            .OverridePropertyName(ChangeRequestRules.OperationCodes);

        // RA-682: an update's editable details, with the ORS journey's rules (OverseasSiteRequestValidatorBase).
        // Each is checked only when sent; absent means unchanged.
        RuleFor(r => r.SiteName).NotEmpty().MaximumLength(200).When(r => r.SiteName is not null);
        RuleFor(r => r.AddressLine1).NotEmpty().MaximumLength(200).When(r => r.AddressLine1 is not null);
        RuleFor(r => r.AddressLine2).MaximumLength(200);
        RuleFor(r => r.TownOrCity).NotEmpty().MaximumLength(100).When(r => r.TownOrCity is not null);
        RuleFor(r => r.ContactName).NotEmpty().MaximumLength(200).When(r => r.ContactName is not null);
        RuleFor(r => r.ContactEmail)
            .NotEmpty()
            .MaximumLength(254)
            .Matches(OverseasSiteEmail.Pattern)
            .WithMessage("ContactEmail must be a valid email address.")
            .When(r => r.ContactEmail is not null);
        RuleFor(r => r.ContactPhone).MaximumLength(30);
        RuleFor(r => r.RepatriatedLoads).NotEmpty().MaximumLength(5000).When(r => r.RepatriatedLoads is not null);
    }
}
