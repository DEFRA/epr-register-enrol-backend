using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

public class BusinessPlanValidatorTests
{
    private readonly BusinessPlanValidator _validator = new();

    private static PatchBusinessPlanRequest AllPercents(
        int infra = 20,
        int price = 20,
        int coll = 20,
        int comms = 20,
        int markets = 10,
        int uses = 10,
        int other = 0
    ) =>
        new()
        {
            NewInfrastructurePercent = infra,
            PriceSupportPercent = price,
            BusinessCollectionsPercent = coll,
            CommunicationsPercent = comms,
            NewMarketsPercent = markets,
            NewUsesPercent = uses,
            OtherPercent = other,
            NewInfrastructureDetail = infra > 0 ? "Detail" : null,
            PriceSupportDetail = price > 0 ? "Detail" : null,
            BusinessCollectionsDetail = coll > 0 ? "Detail" : null,
            CommunicationsDetail = comms > 0 ? "Detail" : null,
            NewMarketsDetail = markets > 0 ? "Detail" : null,
            NewUsesDetail = uses > 0 ? "Detail" : null,
            OtherDetail = other > 0 ? "Detail" : null,
        };

    [Fact]
    public void AllPercentsSumTo100_PassesValidation()
    {
        var result = _validator.TestValidate(AllPercents());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void AllPercentsSumTo90_FailsValidation()
    {
        var request = AllPercents(uses: 0); // sum = 90
        var result = _validator.TestValidate(request);
        result
            .ShouldHaveValidationErrorFor("Percentages")
            .WithErrorMessage("Percentages must total 100.");
    }

    [Fact]
    public void PartialSave_DoesNotEnforceSumTo100()
    {
        var request = AllPercents(uses: 0);
        request.IsPartialSave = true;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor("Percentages");
    }

    [Fact]
    public void PercentAbove100_FailsRangeValidation()
    {
        var request = AllPercents(infra: 110);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.NewInfrastructurePercent);
    }

    [Fact]
    public void NegativePercent_FailsRangeValidation()
    {
        var request = AllPercents(infra: -1);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.NewInfrastructurePercent);
    }

    [Fact]
    public void DetailExceeds500Chars_FailsValidation()
    {
        var request = AllPercents();
        request.NewInfrastructureDetail = new string('x', 501);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.NewInfrastructureDetail);
    }

    [Fact]
    public void DetailExactly500Chars_PassesValidation()
    {
        var request = AllPercents();
        request.NewInfrastructureDetail = new string('x', 500);
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.NewInfrastructureDetail);
    }

    [Fact]
    public void PercentAboveZero_EmptyDetail_FailsValidation()
    {
        var request = AllPercents();
        request.NewInfrastructureDetail = null;
        var result = _validator.TestValidate(request);
        result
            .ShouldHaveValidationErrorFor(r => r.NewInfrastructureDetail)
            .WithErrorMessage("Detail is required when percentage is greater than 0.");
    }

    [Fact]
    public void PercentZero_EmptyDetail_PassesValidation()
    {
        var request = AllPercents(infra: 0, uses: 20);
        request.NewInfrastructureDetail = null;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.NewInfrastructureDetail);
    }

    [Fact]
    public void PartialSave_PercentAboveZero_EmptyDetail_PassesValidation()
    {
        var request = AllPercents();
        request.NewInfrastructureDetail = null;
        request.IsPartialSave = true;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.NewInfrastructureDetail);
    }

    [Fact]
    public void MissingPercents_NoSumError_WhenNotPartialSave()
    {
        // Sum-to-100 rule only fires when ALL seven are provided — partial set skips it regardless of IsPartialSave flag.
        var request = new PatchBusinessPlanRequest
        {
            NewInfrastructurePercent = 50,
            PriceSupportPercent = 50,
            // remaining five not provided
        };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor("Percentages");
    }

    // --- Other category (RA-456) ---

    [Fact]
    public void AllPercentsIncludingOtherSumTo100_PassesValidation()
    {
        var request = AllPercents(uses: 5, other: 5); // sum stays 100
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void OtherPercentAbove100_FailsRangeValidation()
    {
        var request = AllPercents(other: 110);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.OtherPercent);
    }

    [Fact]
    public void OtherPercentNegative_FailsRangeValidation()
    {
        var request = AllPercents(other: -1);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.OtherPercent);
    }

    [Fact]
    public void OtherDetailExceeds500Chars_FailsValidation()
    {
        var request = AllPercents();
        request.OtherDetail = new string('x', 501);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.OtherDetail);
    }

    [Fact]
    public void OtherDetailExactly500Chars_PassesValidation()
    {
        var request = AllPercents();
        request.OtherDetail = new string('x', 500);
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.OtherDetail);
    }

    [Fact]
    public void OtherPercentAboveZero_EmptyDetail_FailsValidation()
    {
        var request = AllPercents(uses: 5, other: 5);
        request.OtherDetail = null;
        var result = _validator.TestValidate(request);
        result
            .ShouldHaveValidationErrorFor(r => r.OtherDetail)
            .WithErrorMessage("Detail is required when percentage is greater than 0.");
    }

    [Fact]
    public void OtherPercentZero_EmptyDetail_PassesValidation()
    {
        var request = AllPercents(other: 0);
        request.OtherDetail = null;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.OtherDetail);
    }

    [Fact]
    public void MissingOtherPercentOnly_NoSumError_WhenNotPartialSave()
    {
        // Sum-to-100 rule requires all seven percents, including Other — omitting only Other
        // skips the rule just like omitting any of the original six.
        var request = new PatchBusinessPlanRequest
        {
            NewInfrastructurePercent = 20,
            PriceSupportPercent = 20,
            BusinessCollectionsPercent = 20,
            CommunicationsPercent = 20,
            NewMarketsPercent = 10,
            NewUsesPercent = 10,
            // OtherPercent not provided
        };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor("Percentages");
    }
}
