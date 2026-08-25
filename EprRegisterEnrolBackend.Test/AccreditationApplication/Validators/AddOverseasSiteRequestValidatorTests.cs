using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

public class AddOverseasSiteRequestValidatorTests
{
    private readonly AddOverseasSiteRequestValidator _validator = new();

    private static AddOverseasSiteRequest ValidRequest() =>
        new()
        {
            SiteName = "Test Recycling GmbH",
            AddressLine1 = "Industriestrasse 42",
            TownOrCity = "Hamburg",
            Country = "Germany",
            ContactName = "Hans Müller",
            ContactEmail = "hans@testrecycling.de",
            OperationCodes = ["R3"],
            Code1 = "A1181",
            RepatriatedLoads = "Rejected loads returned within 30 days at our expense.",
        };

    [Fact]
    public void ValidRequest_PassesValidation()
    {
        var result = _validator.TestValidate(ValidRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("Y46")]
    [InlineData("Y47")]
    [InlineData("Y48")]
    [InlineData("Y49")]
    [InlineData("y46")]
    [InlineData("a1181")]
    public void ApprovedBaselOecdCode_PassesValidation(string code)
    {
        // Y46-Y49 are on the approved list but match neither shape the old
        // BaselOecdRegex accepted - this is the bug the membership check fixes.
        // Matching is case-insensitive.
        var request = ValidRequest() with
        {
            Code1 = code,
        };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.Code1);
    }

    [Fact]
    public void EmptyCode1_FailsValidation()
    {
        var request = ValidRequest() with { Code1 = "" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Code1);
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("Z9999")]
    public void UnapprovedCode1_FailsValidation(string code)
    {
        // "Z9999" matches the old shape regex (letter + 4 digits) but is not on the
        // approved list, so it must now be rejected by the membership check.
        var request = ValidRequest() with
        {
            Code1 = code,
        };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Code1);
    }

    [Fact]
    public void UnapprovedCode2_FailsValidation()
    {
        var request = ValidRequest() with { Code2 = "Z9999" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Code2);
    }

    [Fact]
    public void EmptyCode2_PassesValidation()
    {
        var request = ValidRequest() with { Code2 = "" };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.Code2);
    }

    [Fact]
    public void DuplicateCode1AndCode2_FailsValidation()
    {
        var request = ValidRequest() with { Code1 = "A1181", Code2 = "A1181" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Code2");
    }

    [Fact]
    public void DuplicateCode1AndCode3_CaseInsensitive_FailsValidation()
    {
        var request = ValidRequest() with { Code1 = "A1181", Code3 = "a1181" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Code2");
    }

    [Fact]
    public void DistinctCodes_PassesValidation()
    {
        var request = ValidRequest() with { Code1 = "A1181", Code2 = "Y46", Code3 = "B1010" };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor("Code2");
    }

    [Fact]
    public void BlankCode2AndCode3_DoNotCountAsDuplicates()
    {
        var request = ValidRequest() with { Code2 = "", Code3 = "" };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor("Code2");
    }
}
