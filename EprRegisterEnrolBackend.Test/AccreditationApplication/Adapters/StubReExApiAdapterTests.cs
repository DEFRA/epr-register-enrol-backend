using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Organisation.Models;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

/// <summary>
/// Regression coverage for RA-297: StubReExApiAdapter (used wherever IReExApiAdapter is wired
/// without a real ReEx dependency, e.g. the docker-compose e2e environment) built its fixture
/// OverseasSiteModels without setting IsNewSite, so they fell back to the model's default of
/// true — indistinguishable from a wizard-added site. Registry/prior-year sites must report
/// IsNewSite = false, matching HttpReExApiAdapter's live-API mapping.
/// </summary>
public class StubReExApiAdapterTests
{
    [Fact]
    public async Task GetAccreditationAsync_SeededOverseasSites_AreFlaggedIsNewSiteFalse()
    {
        var sut = new StubReExApiAdapter(
            new FakeOrganisationPersistence(),
            EnabledNullLogger<StubReExApiAdapter>.Instance
        );

        var result = await sut.GetAccreditationAsync(
            "50005",
            FakeOrganisationPersistence.Reg50005.ToString(),
            MaterialType.Plastic,
            2027
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.OverseasSites.Should().NotBeEmpty();
        result
            .Value!.OverseasSites.Should()
            .OnlyContain(
                site => site.IsNewSite == false,
                because: "RA-297: registry/prior-year sites are not new sites"
            );
    }

    // RA-424: mirrors HttpReExApiAdapter's FormatAddress(RegisteredAddressDto) coverage — the
    // stub builds this from a different type (Organisation.Models.RegisteredAddressModel) so it
    // needs its own test rather than sharing the live adapter's.
    [Fact]
    public async Task GetAccreditationAsync_MapsCompanyRegisteredAddressFromFakeOrg()
    {
        var sut = new StubReExApiAdapter(
            new FakeOrganisationPersistence(),
            EnabledNullLogger<StubReExApiAdapter>.Instance
        );

        var result = await sut.GetAccreditationAsync(
            "50005",
            FakeOrganisationPersistence.Reg50005.ToString(),
            MaterialType.Plastic,
            2027
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.CompanyRegisteredAddress.Should().Be("Export House, Southampton, SO14 2AQ");
    }

    // RA-444: the real adapter never sets SiteAddress for exporters (they have no UK
    // processing site) — the stub must match, or stub-mode dev/testing masks the bug
    // where frontend nation resolution silently defaults to England for exporters.
    [Fact]
    public async Task GetAccreditationAsync_Exporter_SiteAddressIsNull()
    {
        var sut = new StubReExApiAdapter(
            new FakeOrganisationPersistence(),
            EnabledNullLogger<StubReExApiAdapter>.Instance
        );

        var result = await sut.GetAccreditationAsync(
            "50006",
            FakeOrganisationPersistence.Reg50006.ToString(),
            MaterialType.Glass,
            2027
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.SiteAddress.Should().BeNull();
        result.Value!.CompanyRegisterAddressPostcode.Should().Be("KW2 7LZ");
    }

    [Fact]
    public async Task GetAccreditationAsync_UnknownOrganisationId_FallsBackToStubRegisteredAddress()
    {
        var sut = new StubReExApiAdapter(
            new FakeOrganisationPersistence(),
            EnabledNullLogger<StubReExApiAdapter>.Instance
        );

        var result = await sut.GetAccreditationAsync(
            "not-a-real-org",
            "reg-1",
            MaterialType.Plastic,
            2027
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result
            .Value!.CompanyRegisteredAddress.Should()
            .Be(
                "1 Stub Registered Office, Stubton, ST1 1AB",
                because: "the fallback must read as a registered office, not the SiteAddress fallback text"
            );
    }

    // Numeric organisationId that parses but has no matching seeded org: org stays null, so
    // every org-derived field (name, registered address, waste-processing type, site address)
    // must fall back to the stub defaults rather than throwing on a null org.
    [Fact]
    public async Task GetAccreditationAsync_NumericOrgIdNotInStore_FallsBackToAllDefaults()
    {
        var sut = new StubReExApiAdapter(
            new FakeOrganisationPersistence(),
            EnabledNullLogger<StubReExApiAdapter>.Instance
        );

        var result = await sut.GetAccreditationAsync("999999", "reg-1", MaterialType.Plastic, 2027);

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.OrganisationName.Should().Be("Stub Reprocessing Ltd");
        result.Value!.RegistrationReference.Should().Be("STUB-REG-001");
        result
            .Value!.CompanyRegisteredAddress.Should()
            .Be("1 Stub Registered Office, Stubton, ST1 1AB");
        result.Value!.WasteProcessingType.Should().Be("reprocessor");
        result.Value!.SiteAddress.Should().Be("1 Stub Lane, Stubton, ST1 1AB");
        result.Value!.PermitNumbers.Should().BeEmpty();
        result.Value!.OverseasSites.Should().BeEmpty();
    }

    // Org id resolves to a real seeded org, but the registrationId supplied doesn't match any of
    // that org's registrations — org-level fields (name) must still come from the found org, while
    // every registration-derived field (site address, permits, waste processing type) falls back.
    [Fact]
    public async Task GetAccreditationAsync_RegistrationIdNotFound_OrgFieldsPopulatedButRegistrationFieldsFallBack()
    {
        var sut = new StubReExApiAdapter(
            new FakeOrganisationPersistence(),
            EnabledNullLogger<StubReExApiAdapter>.Instance
        );

        var result = await sut.GetAccreditationAsync(
            "50001",
            "not-a-real-registration-id",
            MaterialType.Plastic,
            2027
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.OrganisationName.Should().Be("NEWDEV RECYCLING LIMITED");
        result
            .Value!.SiteAddress.Should()
            .Be(
                "1 Stub Lane, Stubton, ST1 1AB",
                because: "no matching registration means SiteAddress falls back, not to null (that's the exporter-only path)"
            );
        result.Value!.WasteProcessingType.Should().Be("reprocessor");
        result.Value!.PermitNumbers.Should().BeEmpty();
        result.Value!.GlassRecyclingProcess.Should().BeNull();
        result.Value!.OverseasSites.Should().BeEmpty();
    }

    // RA-307: an unrecognised wire value for GlassRecyclingProcess must not fail the lookup or
    // get surfaced as a made-up enum value — it should be dropped, mirroring
    // HttpReExApiAdapter's UnrecognisedGlassRecyclingProcessValue_MapsToNull test.
    [Fact]
    public async Task GetAccreditationAsync_UnrecognisedGlassRecyclingProcessValue_MapsToNull()
    {
        var fakeOrgs = new FakeOrganisationPersistence();
        var registrationId = ObjectId.GenerateNewId();
        await fakeOrgs.CreateAsync(
            new OrganisationModel
            {
                OrgId = 60001,
                CompanyDetails = new CompanyDetailsModel { Name = "Glass Unknown Ltd" },
                Registrations =
                [
                    new RegistrationModel
                    {
                        Id = registrationId,
                        Status = "created",
                        Material = "glass",
                        GlassRecyclingProcess = "glass_pulverise",
                        WasteProcessingType = "reprocessor",
                        WasteManagementPermits =
                        [
                            new WasteManagementPermitModel { PermitNumber = "   " },
                            new WasteManagementPermitModel { PermitNumber = "WML-VALID" },
                        ],
                    },
                ],
            }
        );
        var sut = new StubReExApiAdapter(fakeOrgs, EnabledNullLogger<StubReExApiAdapter>.Instance);

        var result = await sut.GetAccreditationAsync(
            "60001",
            registrationId.ToString(),
            MaterialType.Glass,
            2027
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result
            .Value!.GlassRecyclingProcess.Should()
            .BeNull(
                because: "an unrecognised wire value should not fail the whole accreditation lookup"
            );
        result
            .Value!.PermitNumbers.Should()
            .BeEquivalentTo(
                ["WML-VALID"],
                because: "a whitespace-only permit number must be filtered out"
            );
    }

    // StubSiteData only has 4 entries — a 5th overseas site must fall back to a generic "Unknown"
    // country classification rather than indexing out of range. Also covers the SiteId fallback
    // when the site's raw id string doesn't parse as an int.
    [Fact]
    public async Task GetAccreditationAsync_MoreOverseasSitesThanStubData_FifthSiteUsesUnknownFallback()
    {
        var fakeOrgs = new FakeOrganisationPersistence();
        var registrationId = ObjectId.GenerateNewId();
        await fakeOrgs.CreateAsync(
            new OrganisationModel
            {
                OrgId = 60002,
                CompanyDetails = new CompanyDetailsModel { Name = "Many Sites Exports Ltd" },
                Registrations =
                [
                    new RegistrationModel
                    {
                        Id = registrationId,
                        Status = "created",
                        Material = "plastic",
                        WasteProcessingType = "exporter",
                        OverseasSites = ["not-a-number", "900002", "900003", "900004", "900005"],
                    },
                ],
            }
        );
        var sut = new StubReExApiAdapter(fakeOrgs, EnabledNullLogger<StubReExApiAdapter>.Instance);

        var result = await sut.GetAccreditationAsync(
            "60002",
            registrationId.ToString(),
            MaterialType.Plastic,
            2027
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.OverseasSites.Should().HaveCount(5);

        var firstSite = result.Value!.OverseasSites[0];
        firstSite
            .SiteId.Should()
            .Be(900001, because: "a non-numeric raw site id falls back to 900001 + its index (0)");
        firstSite
            .OrsId.Should()
            .Be("not-a-number", because: "OrsId is the raw id regardless of parse success");

        var fifthSite = result.Value!.OverseasSites[4];
        fifthSite
            .Country.Should()
            .Be("Unknown", because: "StubSiteData only defines 4 entries (indices 0-3)");
        fifthSite.IsEu.Should().BeFalse();
        fifthSite.IsOecd.Should().BeFalse();
        fifthSite.SiteId.Should().Be(900005, because: "\"900005\" parses cleanly as an int");
        fifthSite.OrsId.Should().Be("900005");
    }

    // ---------------- RA-475: GetOrganisationNumberAsync ----------------

    [Fact]
    public async Task GetOrganisationNumberAsync_NumericOrganisationId_ReturnsParsedOrgId()
    {
        var sut = new StubReExApiAdapter(
            new FakeOrganisationPersistence(),
            EnabledNullLogger<StubReExApiAdapter>.Instance
        );

        var result = await sut.GetOrganisationNumberAsync(
            "50005",
            TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value.Should().Be(50005);
    }

    [Fact]
    public async Task GetOrganisationNumberAsync_UuidOrganisationId_ReturnsSuccessWithNullValue()
    {
        var sut = new StubReExApiAdapter(
            new FakeOrganisationPersistence(),
            EnabledNullLogger<StubReExApiAdapter>.Instance
        );

        var result = await sut.GetOrganisationNumberAsync(
            "c14854d9-e20a-41b3-87d5-a1fd4bbe2153",
            TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value.Should().BeNull();
    }
}
