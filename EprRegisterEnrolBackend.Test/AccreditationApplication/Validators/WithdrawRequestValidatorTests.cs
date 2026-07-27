using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

public class WithdrawRequestValidatorTests
{
    private readonly WithdrawRequestValidator _validator = new();

    [Fact]
    public void ValidRequest_PassesValidation()
    {
        var request = new WithdrawRequest { Reason = "No longer required" };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyReason_FailsValidation()
    {
        var request = new WithdrawRequest { Reason = "" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Reason);
    }

    [Fact]
    public void WhitespaceReason_FailsValidation()
    {
        var request = new WithdrawRequest { Reason = "   " };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Reason);
    }

    [Fact]
    public void ReasonOf200Words_PassesValidation()
    {
        var request = new WithdrawRequest
        {
            Reason = string.Join(' ', Enumerable.Repeat("word", 200)),
        };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ReasonOver200Words_FailsValidation()
    {
        var request = new WithdrawRequest
        {
            Reason = string.Join(' ', Enumerable.Repeat("word", 201)),
        };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Reason);
    }
}
