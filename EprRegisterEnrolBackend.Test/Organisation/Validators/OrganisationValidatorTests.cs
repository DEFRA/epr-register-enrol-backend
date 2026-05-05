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
            OrgId = 1,
            SchemaVersion = 1,
            Version = 1,
            CompanyDetails = new CompanyDetailsModel { Name = "Operator Export Company" }
        };

        var result = _validator.TestValidate(model);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ValidModel_NoCompanyDetails()
    {
        var model = new OrganisationModel { OrgId = 42, SchemaVersion = 1, Version = 1 };

        var result = _validator.TestValidate(model);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void InvalidOrgId_Zero()
    {
        var model = new OrganisationModel { OrgId = 0, SchemaVersion = 1, Version = 1 };

        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(m => m.OrgId);
    }

    [Fact]
    public void InvalidOrgId_Negative()
    {
        var model = new OrganisationModel { OrgId = -5, SchemaVersion = 1, Version = 1 };

        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(m => m.OrgId);
    }

    [Fact]
    public void InvalidSchemaVersion_Zero()
    {
        var model = new OrganisationModel { OrgId = 1, SchemaVersion = 0, Version = 1 };

        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(m => m.SchemaVersion);
    }

    [Fact]
    public void InvalidVersion_Zero()
    {
        var model = new OrganisationModel { OrgId = 1, SchemaVersion = 1, Version = 0 };

        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(m => m.Version);
    }

    [Fact]
    public void InvalidCompanyDetailsName_TooLong()
    {
        var model = new OrganisationModel
        {
            OrgId = 1,
            SchemaVersion = 1,
            Version = 1,
            CompanyDetails = new CompanyDetailsModel { Name = new string('A', 201) }
        };

        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(m => m.CompanyDetails!.Name);
    }

    [Fact]
    public void InvalidCompanyDetailsName_Empty()
    {
        var model = new OrganisationModel
        {
            OrgId = 1,
            SchemaVersion = 1,
            Version = 1,
            CompanyDetails = new CompanyDetailsModel { Name = "" }
        };

        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(m => m.CompanyDetails!.Name);
    }
}
