using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

public class PatchPrnsRequestValidatorTests
{
    private readonly PatchPrnsRequestValidator _validator = new();

    [Fact]
    public void ValidRequest_PassesValidation()
    {
        var request = new PatchPrnsRequest
        {
            PlannedTonnageBand = PlannedTonnageBand.UpTo1000,
            Authorisers = [new PrnsAuthoriser { FullName = "Jane Smith", Email = "jane@example.com" }]
        };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyRequest_PassesValidation()
    {
        var result = _validator.TestValidate(new PatchPrnsRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void AuthoriserWithEmptyName_FailsValidation()
    {
        var request = new PatchPrnsRequest
        {
            Authorisers = [new PrnsAuthoriser { FullName = "", Email = "jane@example.com" }]
        };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Authorisers[0].FullName");
    }

    [Fact]
    public void AuthoriserWithInvalidEmail_FailsValidation()
    {
        var request = new PatchPrnsRequest
        {
            Authorisers = [new PrnsAuthoriser { FullName = "Jane", Email = "not-valid" }]
        };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Authorisers[0].Email");
    }
}
