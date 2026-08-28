using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

public class PromoteOverseasSiteRequestValidatorTests
{
    private readonly PromoteOverseasSiteRequestValidator _validator = new();

    private static PromoteOverseasSiteRequest ValidRequest() =>
        new()
        {
            SiteName = "Promoted Recycling GmbH",
            AddressLine1 = "Neue Strasse 1",
            TownOrCity = "Munich",
            Country = "Germany",
            ContactName = "Greta Schmidt",
            ContactEmail = "greta@promotedrecycling.de",
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
        var request = ValidRequest() with { Code1 = code };
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
        var request = ValidRequest() with { Code1 = code };
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
        var request = ValidRequest() with
        {
            Code1 = "A1181",
            Code2 = "Y46",
            Code3 = "B1010",
        };
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

    [Fact]
    public void NullCoordinates_PassesValidation()
    {
        var request = ValidRequest() with { Coordinates = null };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.Coordinates);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceCoordinates_PassesValidation(string coordinates)
    {
        var request = ValidRequest() with { Coordinates = coordinates };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.Coordinates);
    }

    [Theory]
    [InlineData("51.5034, -0.1275")]
    [InlineData("-90.0000, 180.0000")]
    [InlineData("52.520008,13.404954")]
    [InlineData("51.5034 , -0.1275")]
    public void CoordinatesWithAtLeast4DecimalPlaces_PassesValidation(string coordinates)
    {
        var request = ValidRequest() with { Coordinates = coordinates };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.Coordinates);
    }

    [Theory]
    [InlineData("not-valid")]
    [InlineData("51.5034")]
    [InlineData("51.503, -0.127")]
    [InlineData("51.5, -0.1275")]
    public void CoordinatesWithFewerThan4DecimalPlacesOrBadFormat_FailsValidation(
        string coordinates
    )
    {
        // RA-479: the promote path writes Coordinates to the same field as add-ORS
        // (ApplyPromotedFields), so it must enforce the same precision rule — this used
        // to be the gap where only AddOverseasSiteRequestValidator checked it.
        var request = ValidRequest() with { Coordinates = coordinates };
        var result = _validator.TestValidate(request);
        result
            .ShouldHaveValidationErrorFor(r => r.Coordinates)
            .WithErrorMessage(
                "Coordinates must be latitude and longitude to at least 4 decimal places, separated by a comma, e.g. 51.5034, -0.1275."
            );
    }

    [Theory]
    [InlineData("91.0000, 0.0000")]
    [InlineData("0.0000, 181.0000")]
    public void CoordinatesOutOfRange_FailsValidation(string coordinates)
    {
        var request = ValidRequest() with { Coordinates = coordinates };
        var result = _validator.TestValidate(request);
        result
            .ShouldHaveValidationErrorFor(r => r.Coordinates)
            .WithErrorMessage(
                "Coordinates latitude must be between -90 and 90 and longitude must be between -180 and 180."
            );
    }

    // RA-486: OperationCodes moved onto RecyclingOperationCodes.AllCodes (R3/R4/R5/R12/R13),
    // narrowing what this endpoint accepts from the old hardcoded R1-R13. R1/R2/R6-R11 are now
    // rejected where they previously passed.

    [Fact]
    public void EmptyOperationCodes_FailsValidation()
    {
        var request = ValidRequest() with { OperationCodes = [] };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.OperationCodes);
    }

    [Theory]
    [InlineData("R1")]
    [InlineData("R2")]
    [InlineData("R6")]
    [InlineData("BOGUS")]
    public void OperationCodesWithCodeOutsideAllCodes_FailsValidation(string code)
    {
        var request = ValidRequest() with { OperationCodes = [code] };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.OperationCodes);
    }

    [Fact]
    public void OperationCodesWithOnlyR12AndR13_FailsValidation()
    {
        var request = ValidRequest() with { OperationCodes = ["R12", "R13"] };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.OperationCodes);
    }

    [Theory]
    [InlineData("R3")]
    [InlineData("R4")]
    [InlineData("R5")]
    public void OperationCodesWithMandatoryOrsCode_PassesValidation(string code)
    {
        var request = ValidRequest() with { OperationCodes = [code] };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.OperationCodes);
    }

    [Fact]
    public void OperationCodesWithMandatoryOrsCodeAndOptionalCode_PassesValidation()
    {
        var request = ValidRequest() with { OperationCodes = ["R3", "R12"] };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.OperationCodes);
    }
}
