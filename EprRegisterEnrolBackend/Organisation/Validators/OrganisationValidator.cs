using EprRegisterEnrolBackend.Organisation.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.Organisation.Validators;

public class OrganisationValidator : AbstractValidator<OrganisationModel>
{
    public OrganisationValidator()
    {
        RuleFor(model => model.OrgId)
            .GreaterThan(0)
            .WithMessage("OrgId must be a positive integer.");

        RuleFor(model => model.SchemaVersion)
            .GreaterThanOrEqualTo(1)
            .WithMessage("SchemaVersion must be at least 1.");

        RuleFor(model => model.Version)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Version must be at least 1.");

        When(model => model.CompanyDetails?.Name is not null, () =>
        {
            RuleFor(model => model.CompanyDetails!.Name)
                .Length(1, 200)
                .WithMessage("CompanyDetails.Name must be between 1 and 200 characters.");
        });
    }
}
