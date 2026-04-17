using System.Collections.Generic;
using EprRegisterEnrolBackend.Organisation.Models;
using EprRegisterEnrolBackend.Organisation.Validators;
using FluentValidation.TestHelper;
using Xunit;

namespace EprRegisterEnrolBackend.Test.Organisation.Validators;

public class OrganisationValidatorTests
{
    private readonly OrganisationValidator _validator = new();

    [Fact]
    public void ValidModel()
    {
        var model = new OrganisationModel
        {
            CompanyName = "Operator Export Company",
            CompaniesHouseNumber = "11044891",
            SchemeRegistrationId = "BN2712300000001",
            RegisteredAddress = "29 Acacia Road",
            ApprovedPerson = "General Blight",
            Directors = new List<DirectorModel>
            {
                new() { Name = "Eric Twinge" }
            }
        };

        var result = _validator.TestValidate(model);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void MissingCompanyName()
    {
        var model = new OrganisationModel
        {
            CompanyName = "",
            CompaniesHouseNumber = "11044891",
            SchemeRegistrationId = "BN2712300000001",
            RegisteredAddress = "29 Acacia Road",
            ApprovedPerson = "General Blight"
        };

        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(m => m.CompanyName);
    }

    [Fact]
    public void EmptyCompaniesHouseNumber()
    {
        var model = new OrganisationModel
        {
            CompanyName = "Operator Export Company",
            CompaniesHouseNumber = "",
            SchemeRegistrationId = "BN2712300000001",
            RegisteredAddress = "29 Acacia Road",
            ApprovedPerson = "General Blight"
        };

        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(m => m.CompaniesHouseNumber);
    }

    [Fact]
    public void MissingSchemeRegistrationId()
    {
        var model = new OrganisationModel
        {
            CompanyName = "Operator Export Company",
            CompaniesHouseNumber = "11044891",
            SchemeRegistrationId = "",
            RegisteredAddress = "29 Acacia Road",
            ApprovedPerson = "General Blight"
        };

        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(m => m.SchemeRegistrationId);
    }

    [Fact]
    public void MissingRegisteredAddress()
    {
        var model = new OrganisationModel
        {
            CompanyName = "Operator Export Company",
            CompaniesHouseNumber = "11044891",
            SchemeRegistrationId = "BN2712300000001",
            RegisteredAddress = "",
            ApprovedPerson = "General Blight"
        };

        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(m => m.RegisteredAddress);
    }

    [Fact]
    public void MissingApprovedPerson()
    {
        var model = new OrganisationModel
        {
            CompanyName = "Operator Export Company",
            CompaniesHouseNumber = "11044891",
            SchemeRegistrationId = "BN2712300000001",
            RegisteredAddress = "29 Acacia Road",
            ApprovedPerson = ""
        };

        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(m => m.ApprovedPerson);
    }
}
