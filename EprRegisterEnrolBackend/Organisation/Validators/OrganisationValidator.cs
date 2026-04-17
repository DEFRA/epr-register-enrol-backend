using EprRegisterEnrolBackend.Organisation.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.Organisation.Validators;

public class OrganisationValidator : AbstractValidator<OrganisationModel>
{
    public OrganisationValidator()
    {
        RuleFor(model => model.CompanyName)
            .Matches(@"^[\w\s]+$")
            .Length(3, 200)
            .WithMessage("CompanyName was not valid. Must be between 3 and 200 characters and contain only letters, numbers and whitespace.");

        RuleFor(model => model.CompaniesHouseNumber).NotEmpty();

        RuleFor(model => model.SchemeRegistrationId).NotEmpty();

        RuleFor(model => model.RegisteredAddress).NotEmpty();

        RuleFor(model => model.ApprovedPerson).NotEmpty();
    }
}
