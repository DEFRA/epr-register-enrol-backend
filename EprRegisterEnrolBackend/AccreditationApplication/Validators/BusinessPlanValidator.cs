using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class BusinessPlanValidator : AbstractValidator<PatchBusinessPlanRequest>
{
    public BusinessPlanValidator()
    {
        RuleFor(r => r.NewInfrastructurePercent)
            .InclusiveBetween(0, 100)
            .When(r => r.NewInfrastructurePercent.HasValue);
        RuleFor(r => r.PriceSupportPercent)
            .InclusiveBetween(0, 100)
            .When(r => r.PriceSupportPercent.HasValue);
        RuleFor(r => r.BusinessCollectionsPercent)
            .InclusiveBetween(0, 100)
            .When(r => r.BusinessCollectionsPercent.HasValue);
        RuleFor(r => r.CommunicationsPercent)
            .InclusiveBetween(0, 100)
            .When(r => r.CommunicationsPercent.HasValue);
        RuleFor(r => r.NewMarketsPercent)
            .InclusiveBetween(0, 100)
            .When(r => r.NewMarketsPercent.HasValue);
        RuleFor(r => r.NewUsesPercent)
            .InclusiveBetween(0, 100)
            .When(r => r.NewUsesPercent.HasValue);
        RuleFor(r => r.OtherPercent)
            .InclusiveBetween(0, 100)
            .When(r => r.OtherPercent.HasValue);

        RuleFor(r => r.NewInfrastructureDetail)
            .MaximumLength(500)
            .When(r => r.NewInfrastructureDetail != null);
        RuleFor(r => r.PriceSupportDetail)
            .MaximumLength(500)
            .When(r => r.PriceSupportDetail != null);
        RuleFor(r => r.BusinessCollectionsDetail)
            .MaximumLength(500)
            .When(r => r.BusinessCollectionsDetail != null);
        RuleFor(r => r.CommunicationsDetail)
            .MaximumLength(500)
            .When(r => r.CommunicationsDetail != null);
        RuleFor(r => r.NewMarketsDetail).MaximumLength(500).When(r => r.NewMarketsDetail != null);
        RuleFor(r => r.NewUsesDetail).MaximumLength(500).When(r => r.NewUsesDetail != null);
        RuleFor(r => r.OtherDetail).MaximumLength(500).When(r => r.OtherDetail != null);

        // Detail field required when corresponding percentage > 0 (non-partial saves only).
        When(
            r => !r.IsPartialSave,
            () =>
            {
                RuleFor(r => r.NewInfrastructureDetail)
                    .NotEmpty()
                    .WithMessage("Detail is required when percentage is greater than 0.")
                    .When(r => r.NewInfrastructurePercent > 0);
                RuleFor(r => r.PriceSupportDetail)
                    .NotEmpty()
                    .WithMessage("Detail is required when percentage is greater than 0.")
                    .When(r => r.PriceSupportPercent > 0);
                RuleFor(r => r.BusinessCollectionsDetail)
                    .NotEmpty()
                    .WithMessage("Detail is required when percentage is greater than 0.")
                    .When(r => r.BusinessCollectionsPercent > 0);
                RuleFor(r => r.CommunicationsDetail)
                    .NotEmpty()
                    .WithMessage("Detail is required when percentage is greater than 0.")
                    .When(r => r.CommunicationsPercent > 0);
                RuleFor(r => r.NewMarketsDetail)
                    .NotEmpty()
                    .WithMessage("Detail is required when percentage is greater than 0.")
                    .When(r => r.NewMarketsPercent > 0);
                RuleFor(r => r.NewUsesDetail)
                    .NotEmpty()
                    .WithMessage("Detail is required when percentage is greater than 0.")
                    .When(r => r.NewUsesPercent > 0);
                RuleFor(r => r.OtherDetail)
                    .NotEmpty()
                    .WithMessage("Detail is required when percentage is greater than 0.")
                    .When(r => r.OtherPercent > 0);
            }
        );

        // Sum-to-100 only when all six values are provided and this is not a partial save.
        When(
            r => !r.IsPartialSave && AllPercentsProvided(r),
            () =>
            {
                RuleFor(r => r)
                    .Must(r => SumOfPercents(r) == 100)
                    .WithName("Percentages")
                    .WithMessage("Percentages must total 100.");
            }
        );
    }

    private static bool AllPercentsProvided(PatchBusinessPlanRequest r) =>
        r.NewInfrastructurePercent.HasValue
        && r.PriceSupportPercent.HasValue
        && r.BusinessCollectionsPercent.HasValue
        && r.CommunicationsPercent.HasValue
        && r.NewMarketsPercent.HasValue
        && r.NewUsesPercent.HasValue
        && r.OtherPercent.HasValue;

    private static int SumOfPercents(PatchBusinessPlanRequest r) =>
        (r.NewInfrastructurePercent ?? 0)
        + (r.PriceSupportPercent ?? 0)
        + (r.BusinessCollectionsPercent ?? 0)
        + (r.CommunicationsPercent ?? 0)
        + (r.NewMarketsPercent ?? 0)
        + (r.NewUsesPercent ?? 0)
        + (r.OtherPercent ?? 0);
}
