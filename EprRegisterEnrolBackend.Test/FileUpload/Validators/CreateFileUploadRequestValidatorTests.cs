using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.FileUpload.Models;
using EprRegisterEnrolBackend.FileUpload.Validators;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.FileUpload.Validators;

public class CreateFileUploadRequestValidatorTests
{
    private readonly CreateFileUploadRequestValidator _validator = new();

    private static CreateFileUploadRequest ValidRequest() => new()
    {
        OrganisationId = "org-123",
        Material = MaterialType.Steel,
        YearOfAccreditation = DateTime.UtcNow.Year,
        FileId = "cdp-upload-id-abc",
        Filename = "report.pdf",
        ContentType = "application/pdf",
        S3Key = "file-uploads/Steel/2025/report.pdf"
    };

    [Fact]
    public void ValidRequest_PassesValidation()
    {
        var result = _validator.TestValidate(ValidRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyOrganisationId_FailsValidation()
    {
        var request = ValidRequest();
        request.OrganisationId = string.Empty;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.OrganisationId);
    }

    [Fact]
    public void EmptyFileId_FailsValidation()
    {
        var request = ValidRequest();
        request.FileId = string.Empty;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.FileId);
    }

    [Fact]
    public void EmptyFilename_FailsValidation()
    {
        var request = ValidRequest();
        request.Filename = string.Empty;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Filename);
    }

    [Fact]
    public void FilenameWithInvalidChars_FailsValidation()
    {
        var request = ValidRequest();
        request.Filename = "../../etc/passwd";
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Filename);
    }

    [Fact]
    public void FilenameExceeding255Chars_FailsValidation()
    {
        var request = ValidRequest();
        request.Filename = new string('a', 256) + ".pdf";
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Filename);
    }

    [Fact]
    public void InvalidContentType_FailsValidation()
    {
        var request = ValidRequest();
        request.ContentType = "application/executable";
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.ContentType);
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/msword")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    public void AllPermittedContentTypes_PassValidation(string contentType)
    {
        var request = ValidRequest();
        request.ContentType = contentType;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.ContentType);
    }

    [Fact]
    public void YearTooFarInPast_FailsValidation()
    {
        var request = ValidRequest();
        request.YearOfAccreditation = DateTime.UtcNow.Year - 3;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.YearOfAccreditation);
    }

    [Fact]
    public void FutureYear_FailsValidation()
    {
        var request = ValidRequest();
        request.YearOfAccreditation = DateTime.UtcNow.Year + 1;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.YearOfAccreditation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-2)]
    public void YearWithinRange_PassesValidation(int yearsOffset)
    {
        var request = ValidRequest();
        request.YearOfAccreditation = DateTime.UtcNow.Year + yearsOffset;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.YearOfAccreditation);
    }

    [Fact]
    public void EmptyS3Key_FailsValidation()
    {
        var request = ValidRequest();
        request.S3Key = string.Empty;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.S3Key);
    }
}
