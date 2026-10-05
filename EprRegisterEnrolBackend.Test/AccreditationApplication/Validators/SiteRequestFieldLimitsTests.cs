using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using FluentValidation;
using FluentValidation.TestHelper;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Validators;

// RA-620: pins the length limits the frontend wizard mirrors. Two production 400s came from the
// frontend accepting values these validators reject (a 34-character phone number against a limit
// of 30), and nothing here failed when a limit changed, so the frontend could drift again without
// anyone noticing.
//
// If one of these tests has to change, the matching constant in the frontend's
// src/server/common/constants/siteFieldLimits.js (and the wizard step that enforces it) must
// change with it.
public class SiteRequestFieldLimitsTests
{
    private const string MaxLengthErrorCode = "MaximumLengthValidator";

    private static AddOverseasSiteRequest ValidAddOverseasSite() =>
        new()
        {
            SiteName = "Test Recycling GmbH",
            AddressLine1 = "Industriestrasse 42",
            TownOrCity = "Hamburg",
            Country = "Germany",
            ContactName = "Hans Müller",
            ContactEmail = "hans@testrecycling.de",
            OperationCodes = ["R3"],
            Code1 = "A1181",
            RepatriatedLoads = "Rejected loads returned within 30 days at our expense.",
        };

    private static PromoteOverseasSiteRequest ValidPromoteOverseasSite() =>
        new()
        {
            SiteName = "Promoted Recycling GmbH",
            AddressLine1 = "Neue Strasse 1",
            TownOrCity = "Munich",
            Country = "Germany",
            ContactName = "Greta Schmidt",
            ContactEmail = "greta@promotedrecycling.de",
            OperationCodes = ["R3"],
            Code1 = "A1181",
            RepatriatedLoads = "Rejected loads returned within 30 days at our expense.",
        };

    private static AddInterimSiteRequest ValidInterimSite() =>
        new()
        {
            Country = "France",
            SiteName = "Interim Recycling Site",
            AddressLine1 = "1 Rue Example",
            TownOrCity = "Paris",
            ContactName = "Jane Smith",
            ContactEmail = "jane.smith@example.com",
            ContactPhone = "+33 1 23 45 67 89",
            OperationCodes = ["R12"],
        };

    // An address of exactly `length` characters that still passes the email format rule.
    private static string EmailOfLength(int length) =>
        $"{new string('a', length - "@example.com".Length)}@example.com";

    private static void SetProperty<T>(T request, string property, string value) =>
        typeof(T).GetProperty(property)!.SetValue(request, value);

    // The value at the limit is accepted and one character more is refused by the length rule
    // itself, rather than by some other rule on the same field.
    private static void AssertLimit<T>(
        IValidator<T> validator,
        Func<T> validRequest,
        string property,
        string atLimit,
        string overLimit
    )
    {
        var accepted = validRequest();
        SetProperty(accepted, property, atLimit);
        validator.TestValidate(accepted).ShouldNotHaveValidationErrorFor(property);

        var refused = validRequest();
        SetProperty(refused, property, overLimit);
        validator
            .TestValidate(refused)
            .ShouldHaveValidationErrorFor(property)
            .WithErrorCode(MaxLengthErrorCode);
    }

    private static string Filler(int length) => new('a', length);

    // Overseas site create, update and promote share OverseasSiteRequestValidatorBase, so the
    // same limits are asserted against both concrete validators.
    [Theory]
    [InlineData("SiteName", 200)]
    [InlineData("AddressLine1", 200)]
    [InlineData("AddressLine2", 200)]
    [InlineData("TownOrCity", 100)]
    [InlineData("Country", 100)]
    [InlineData("ContactName", 200)]
    [InlineData("ContactPhone", 30)]
    [InlineData("RepatriatedLoads", 5000)]
    public void OverseasSite_StringLimits_AreEnforcedOnCreateAndPromote(string property, int max)
    {
        AssertLimit(
            new AddOverseasSiteRequestValidator(),
            ValidAddOverseasSite,
            property,
            Filler(max),
            Filler(max + 1)
        );
        AssertLimit(
            new PromoteOverseasSiteRequestValidator(),
            ValidPromoteOverseasSite,
            property,
            Filler(max),
            Filler(max + 1)
        );
    }

    [Fact]
    public void OverseasSite_ContactEmail_Allows254CharactersAndRefuses255()
    {
        AssertLimit(
            new AddOverseasSiteRequestValidator(),
            ValidAddOverseasSite,
            "ContactEmail",
            EmailOfLength(254),
            EmailOfLength(255)
        );
        AssertLimit(
            new PromoteOverseasSiteRequestValidator(),
            ValidPromoteOverseasSite,
            "ContactEmail",
            EmailOfLength(254),
            EmailOfLength(255)
        );
    }

    [Theory]
    [InlineData("Country", 100)]
    [InlineData("SiteName", 200)]
    [InlineData("AddressLine1", 200)]
    [InlineData("AddressLine2", 200)]
    [InlineData("TownOrCity", 100)]
    [InlineData("StateOrRegion", 100)]
    [InlineData("Postcode", 20)]
    [InlineData("ContactName", 200)]
    public void InterimSite_StringLimits_AreEnforced(string property, int max)
    {
        AssertLimit(
            new AddInterimSiteRequestValidator(),
            ValidInterimSite,
            property,
            Filler(max),
            Filler(max + 1)
        );
    }

    [Fact]
    public void InterimSite_ContactEmail_Allows254CharactersAndRefuses255()
    {
        AssertLimit(
            new AddInterimSiteRequestValidator(),
            ValidInterimSite,
            "ContactEmail",
            EmailOfLength(254),
            EmailOfLength(255)
        );
    }

    // The interim site phone is held to its own pattern, ^\+?[0-9()\-\s]{7,20}$, which is
    // stricter than the 30-character cap: 7 to 20 characters after an optional leading "+". The
    // frontend's interim wizard enforces exactly this, so the edges are pinned here.
    [Theory]
    [InlineData("1234567", true)]
    [InlineData("12345678901234567890", true)]
    [InlineData("+1234567", true)]
    [InlineData("+12345678901234567890", true)]
    [InlineData("123456", false)]
    [InlineData("123456789012345678901", false)]
    [InlineData("+123456", false)]
    [InlineData("+123456789012345678901", false)]
    [InlineData("1234567+890", false)]
    public void InterimSite_ContactPhone_AllowsSevenToTwentyCharactersAfterAnOptionalLeadingPlus(
        string phone,
        bool valid
    )
    {
        var request = ValidInterimSite();
        request.ContactPhone = phone;

        var result = new AddInterimSiteRequestValidator().TestValidate(request);

        if (valid)
            result.ShouldNotHaveValidationErrorFor(r => r.ContactPhone);
        else
            result.ShouldHaveValidationErrorFor(r => r.ContactPhone);
    }
}
