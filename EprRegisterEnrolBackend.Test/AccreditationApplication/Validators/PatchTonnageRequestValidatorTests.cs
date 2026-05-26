using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

public class PatchTonnageRequestValidatorTests
{
    private readonly PatchTonnageRequestValidator _validator = new();

    [Fact]
    public void EmptyRequest_PassesValidation()
    {
        var result = _validator.TestValidate(new PatchTonnageRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void AuthorisersOnly_NoPlannedTonnageBand_PassesValidation()
    {
        var request = new PatchTonnageRequest
        {
            Authorisers = [new PrnsAuthoriser { FullName = "Jane Smith", Email = "jane@example.com" }]
        };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void PlannedTonnageBandOnly_NoAuthorisers_PassesValidation()
    {
        var request = new PatchTonnageRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo1000 };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void BothFields_Valid_PassesValidation()
    {
        var request = new PatchTonnageRequest
        {
            PlannedTonnageBand = PlannedTonnageBand.UpTo1000,
            Authorisers = [new PrnsAuthoriser { FullName = "Jane Smith", Email = "jane@example.com" }]
        };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void AuthoriserWithEmptyName_FailsValidation()
    {
        var request = new PatchTonnageRequest
        {
            Authorisers = [new PrnsAuthoriser { FullName = "", Email = "jane@example.com" }]
        };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Authorisers[0].FullName");
    }

    [Fact]
    public void AuthoriserWithInvalidEmail_FailsValidation()
    {
        var request = new PatchTonnageRequest
        {
            Authorisers = [new PrnsAuthoriser { FullName = "Jane", Email = "not-valid" }]
        };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Authorisers[0].Email");
    }
}
