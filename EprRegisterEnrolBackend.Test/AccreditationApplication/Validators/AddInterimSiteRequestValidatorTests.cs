using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

public class AddInterimSiteRequestValidatorTests
{
    private readonly AddInterimSiteRequestValidator _validator = new();

    private static AddInterimSiteRequest ValidRequest() =>
        new()
        {
            Country = "France",
            SiteName = "Interim Recycling Site",
            AddressLine1 = "1 Rue Example",
            AddressLine2 = "Zone Industrielle",
            TownOrCity = "Paris",
            StateOrRegion = "Île-de-France",
            Postcode = "75001",
            ContactName = "Jane Smith",
            ContactEmail = "jane.smith@example.com",
            ContactPhone = "+33 1 23 45 67 89",
        };

    [Fact]
    public void ValidRequest_PassesValidation()
    {
        var result = _validator.TestValidate(ValidRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ValidRequest_WithoutOptionalFields_PassesValidation()
    {
        var request = ValidRequest();
        request.AddressLine2 = null;
        request.StateOrRegion = null;
        request.Postcode = null;

        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyCountry_FailsValidation()
    {
        var request = ValidRequest();
        request.Country = "";
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.Country);
    }

    [Fact]
    public void EmptySiteName_FailsValidation()
    {
        var request = ValidRequest();
        request.SiteName = "";
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.SiteName);
    }

    [Fact]
    public void EmptyAddressLine1_FailsValidation()
    {
        var request = ValidRequest();
        request.AddressLine1 = "";
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.AddressLine1);
    }

    [Fact]
    public void EmptyTownOrCity_FailsValidation()
    {
        var request = ValidRequest();
        request.TownOrCity = "";
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.TownOrCity);
    }

    [Fact]
    public void EmptyContactName_FailsValidation()
    {
        var request = ValidRequest();
        request.ContactName = "";
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.ContactName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.example.com")]
    [InlineData("no-domain@")]
    public void InvalidContactEmail_FailsValidation(string email)
    {
        var request = ValidRequest();
        request.ContactEmail = email;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.ContactEmail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("phone-number")]
    public void InvalidContactPhone_FailsValidation(string phone)
    {
        var request = ValidRequest();
        request.ContactPhone = phone;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(r => r.ContactPhone);
    }

    [Theory]
    [InlineData("+44 7911 123456")]
    [InlineData("01234 567890")]
    [InlineData("(020) 7946 0958")]
    public void ValidContactPhoneFormats_PassValidation(string phone)
    {
        var request = ValidRequest();
        request.ContactPhone = phone;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(r => r.ContactPhone);
    }
}
