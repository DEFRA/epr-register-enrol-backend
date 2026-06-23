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
        int uses = 10
    ) =>
        new()
        {
            NewInfrastructurePercent = infra,
            PriceSupportPercent = price,
            BusinessCollectionsPercent = coll,
            CommunicationsPercent = comms,
            NewMarketsPercent = markets,
            NewUsesPercent = uses,
            NewInfrastructureDetail = infra > 0 ? "Detail" : null,
            PriceSupportDetail = price > 0 ? "Detail" : null,
            BusinessCollectionsDetail = coll > 0 ? "Detail" : null,
            CommunicationsDetail = comms > 0 ? "Detail" : null,
            NewMarketsDetail = markets > 0 ? "Detail" : null,
            NewUsesDetail = uses > 0 ? "Detail" : null,
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
        // Sum-to-100 rule only fires when ALL six are provided — partial set skips it regardless of IsPartialSave flag.
        var request = new PatchBusinessPlanRequest
        {
            NewInfrastructurePercent = 50,
            PriceSupportPercent = 50,
            // remaining four not provided
        };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor("Percentages");
    }
}
