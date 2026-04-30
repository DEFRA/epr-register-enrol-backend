using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation.TestHelper;
using Xunit;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

public class SeedRequestValidatorTests
{
    private readonly SeedRequestValidator _validator = new();

    [Fact]
    public void ValidRequest_PassesValidation()
    {
        var request = new SeedRequest { MaterialType = MaterialType.Steel, Year = 2026 };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(2023)]
    [InlineData(2000)]
    public void YearBefore2024_FailsValidation(int year)
    {
        var request = new SeedRequest { MaterialType = MaterialType.Steel, Year = year };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Year);
    }

    [Fact]
    public void Year2024_PassesValidation()
    {
        var request = new SeedRequest { MaterialType = MaterialType.Wood, Year = 2024 };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.Year);
    }
}
