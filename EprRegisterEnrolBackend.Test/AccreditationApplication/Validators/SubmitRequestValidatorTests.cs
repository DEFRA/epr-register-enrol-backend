using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

public class SubmitRequestValidatorTests
{
    private readonly SubmitRequestValidator _validator = new();

    [Fact]
    public void ValidRequest_PassesValidation()
    {
        var request = new SubmitRequest
        {
            FullName = "Jane Smith",
            JobTitle = "Operations Manager",
            Email = "jane@example.com"
        };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyFullName_FailsValidation()
    {
        var request = new SubmitRequest { FullName = "", JobTitle = "Manager", Email = "a@b.com" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.FullName);
    }

    [Fact]
    public void EmptyJobTitle_FailsValidation()
    {
        var request = new SubmitRequest { FullName = "Jane", JobTitle = "", Email = "a@b.com" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.JobTitle);
    }

    [Fact]
    public void InvalidEmail_FailsValidation()
    {
        var request = new SubmitRequest { FullName = "Jane", JobTitle = "Manager", Email = "not-an-email" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Email);
    }

    [Fact]
    public void EmptyEmail_FailsValidation()
    {
        var request = new SubmitRequest { FullName = "Jane", JobTitle = "Manager", Email = "" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Email);
    }
}
