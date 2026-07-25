using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

public class QueryFromCaseManagementRequestValidatorTests
{
    private readonly QueryFromCaseManagementRequestValidator _validator = new();

    [Fact]
    public void ValidRequest_PassesValidation()
    {
        var request = new QueryFromCaseManagementRequest
        {
            QueryNote = "Please clarify tonnage.",
            SectionKeys = ["business-plan", "authority-to-issue"],
        };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptySectionKeys_FailsValidation()
    {
        var request = new QueryFromCaseManagementRequest { SectionKeys = [] };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.SectionKeys);
    }

    [Fact]
    public void UnrecognisedSectionKey_FailsValidation()
    {
        var request = new QueryFromCaseManagementRequest { SectionKeys = ["not-a-real-key"] };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("SectionKeys[0]");
    }

    [Theory]
    [InlineData("authority-to-issue")]
    [InlineData("prn-tonnage")]
    [InlineData("business-plan")]
    [InlineData("sampling-and-inspection-plan")]
    [InlineData("broadly-equivalent-standards")]
    [InlineData("overseas-reprocessing-sites")]
    public void EachKnownSectionKey_PassesValidation(string key)
    {
        var request = new QueryFromCaseManagementRequest { SectionKeys = [key] };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
