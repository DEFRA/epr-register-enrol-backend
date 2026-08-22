using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

public class PatchRecyclingOperationsRequestValidatorTests
{
    private readonly PatchRecyclingOperationsRequestValidator _validator = new();

    private static PatchRecyclingOperationsRequest ValidRequest(List<string> operationCodes) =>
        new() { OperationCodes = operationCodes };

    [Fact]
    public void ValidRequest_PassesValidation()
    {
        var result = _validator.TestValidate(ValidRequest(["R4"]));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyOperationCodes_FailsValidation()
    {
        var result = _validator.TestValidate(ValidRequest([]));
        result.ShouldHaveValidationErrorFor(r => r.OperationCodes);
    }

    [Fact]
    public void InvalidOperationCode_FailsValidation()
    {
        var result = _validator.TestValidate(ValidRequest(["R1"]));
        result.ShouldHaveValidationErrorFor(r => r.OperationCodes);
    }

    [Fact]
    public void OperationCodesWithR12Alone_FailsValidation()
    {
        var result = _validator.TestValidate(ValidRequest(["R12"]));
        result.ShouldHaveValidationErrorFor(r => r.OperationCodes);
    }

    [Fact]
    public void OperationCodesWithR13Alone_FailsValidation()
    {
        var result = _validator.TestValidate(ValidRequest(["R13"]));
        result.ShouldHaveValidationErrorFor(r => r.OperationCodes);
    }

    [Fact]
    public void OperationCodesWithR12AndR13Alone_FailsValidation()
    {
        var result = _validator.TestValidate(ValidRequest(["R12", "R13"]));
        result.ShouldHaveValidationErrorFor(r => r.OperationCodes);
    }

    [Fact]
    public void OperationCodesWithR12AndR4_PassesValidation()
    {
        var result = _validator.TestValidate(ValidRequest(["R12", "R4"]));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void OperationCodesWithR3AndR5_PassesValidation()
    {
        var result = _validator.TestValidate(ValidRequest(["R3", "R5"]));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void OperationCodesWithR12R13R4_PassesValidation()
    {
        var result = _validator.TestValidate(ValidRequest(["R12", "R13", "R4"]));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void OperationCodesWithR5R12R3_PassesValidation()
    {
        var result = _validator.TestValidate(ValidRequest(["R5", "R12", "R3"]));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void OperationCodesWithBogusCode_FailsValidation()
    {
        var result = _validator.TestValidate(ValidRequest(["R3", "BOGUS"]));
        result.ShouldHaveValidationErrorFor(r => r.OperationCodes);
    }
}
