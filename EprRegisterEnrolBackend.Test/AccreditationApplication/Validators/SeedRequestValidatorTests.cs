using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

public class SeedRequestValidatorTests
{
    private readonly SeedRequestValidator _validator = new();

    [Theory]
    [InlineData(2023)]
    [InlineData(2000)]
    public void YearBefore2024_FailsValidation(int year)
    {
        var request = new SeedRequest { Year = year };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Year);
    }

    [Fact]
    public void Year2024_PassesValidation()
    {
        var request = new SeedRequest { Year = 2024 };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.Year);
    }
}
