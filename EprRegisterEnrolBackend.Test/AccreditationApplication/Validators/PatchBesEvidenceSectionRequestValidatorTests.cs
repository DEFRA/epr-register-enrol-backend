using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

public class PatchBesEvidenceSectionRequestValidatorTests
{
    private readonly PatchBesEvidenceSectionRequestValidator _validator = new();

    [Fact]
    public void EmptyRequest_PassesValidation()
    {
        var result = _validator.TestValidate(new PatchBesEvidenceSectionRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(SectionStatus.NotStarted)]
    [InlineData(SectionStatus.InProgress)]
    [InlineData(SectionStatus.Completed)]
    [InlineData(SectionStatus.Submitted)]
    public void NonQueriedStatus_PassesValidation(SectionStatus status)
    {
        var request = new PatchBesEvidenceSectionRequest { SectionStatus = status };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void QueriedStatus_FailsValidation()
    {
        var request = new PatchBesEvidenceSectionRequest { SectionStatus = SectionStatus.Queried };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.SectionStatus);
    }
}
