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
        result
            .Value!.RegistrationReference.Should()
            .MatchRegex(RegistrationReferenceFormat)
            .And.StartWith(
                "R27ER999999",
                because: "an English reprocessor still gets a reference built from its org number"
            );
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
                OrgId = 70001,
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
            "70001",
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
                OrgId = 70002,
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
            "70002",
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
            .Be(
                "001",
                because: "OrsId is synthesized from position (1-indexed, 3-digit), not the raw "
                    + "site id -- the seed data's 6-digit placeholders are not real ORS ids and "
                    + "would blow OrsIdGenerator's 999 cap if used directly"
            );

        var fifthSite = result.Value!.OverseasSites[4];
        fifthSite
            .Country.Should()
            .Be("Unknown", because: "StubSiteData only defines 4 entries (indices 0-3)");
        fifthSite.IsEu.Should().BeFalse();
        fifthSite.IsOecd.Should().BeFalse();
        fifthSite.SiteId.Should().Be(900005, because: "\"900005\" parses cleanly as an int");
        fifthSite.OrsId.Should().Be("005", because: "the 5th site synthesizes OrsId \"005\"");
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

    // ---------------- RA-526/RA-553: GetNationAsync ----------------

    [Fact]
    public async Task GetNationAsync_NonPerfTestOrgWithNoRegulatorCode_DefaultsToEngland()
    {
        var sut = new StubReExApiAdapter(
            new FakeOrganisationPersistence(),
            EnabledNullLogger<StubReExApiAdapter>.Instance
        );

        var result = await sut.GetNationAsync("1", "reg-1", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value.Should().Be(Nation.England);
    }

    [Fact]
    public async Task GetNationAsync_PerfTestOrg_ResolvesItsAssignedNation()
    {
        var sut = new StubReExApiAdapter(
            new FakeOrganisationPersistence(),
            EnabledNullLogger<StubReExApiAdapter>.Instance
        );

        // OrgId 60002 (perftest reprocessor #2) is seeded with SubmittedToRegulator = "sepa".
        var result = await sut.GetNationAsync(
            "60002",
            "reg-1",
            TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value.Should().Be(Nation.Scotland);
    }

    [Fact]
    public async Task GetAccreditationAsync_PerfTestOrg_ResolvesItsAssignedNation()
    {
        var sut = new StubReExApiAdapter(
            new FakeOrganisationPersistence(),
            EnabledNullLogger<StubReExApiAdapter>.Instance
        );

        // OrgId 60003 (perftest reprocessor #3) is seeded with SubmittedToRegulator = "nrw"
        // and a Cardiff address - RA-553 regression: the fixture used to default Nation to
        // England here regardless of the org's actual regulator/address.
        var org = await new FakeOrganisationPersistence().GetByOrgIdAsync(60003);
        var registrationId = org!.Registrations![0].Id.ToString();

        var result = await sut.GetAccreditationAsync(
            "60003",
            registrationId,
            MaterialType.Plastic,
            2027
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.Nation.Should().Be(Nation.Wales);
    }

    // ---------------- RA-000: e2e- test organisations ----------------

    private static StubReExApiAdapter NewSut() =>
        new(new FakeOrganisationPersistence(), EnabledNullLogger<StubReExApiAdapter>.Instance);

    [Fact]
    public async Task GetAccreditationAsync_TestOrganisation_NamesItAfterItsIdInEngland()
    {
        var result = await NewSut()
            .GetAccreditationAsync("e2e-c0ffee", "e2e-reg-c0ffee", MaterialType.Plastic, 2025);

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.OrganisationName.Should().Be("E2E Organisation e2e-c0ffee");
        result.Value.Nation.Should().Be(Nation.England);
        result.Value.IsExporter.Should().BeFalse();
    }

    [Fact]
    public async Task GetAccreditationAsync_TestOrganisation_UsesOnlyNotifySimulatedEmails()
    {
        var result = await NewSut()
            .GetAccreditationAsync("e2e-c0ffee", "e2e-reg-c0ffee", MaterialType.Plastic, 2025);

        const string simulated = "simulate-delivered@notifications.service.gov.uk";
        result.Value!.SubmitterContactDetails!.Email.Should().Be(simulated);
        result.Value.Prns!.Authorisers.Should().OnlyContain(a => a.Email == simulated);
    }

    [Theory]
    [InlineData("E2E-c0ffee")]
    [InlineData("not-a-test-org")]
    public async Task GetAccreditationAsync_NonTestOrganisation_KeepsTheDefaultStubFixture(
        string organisationId
    )
    {
        var result = await NewSut()
            .GetAccreditationAsync(organisationId, "reg", MaterialType.Plastic, 2025);

        result.Value!.OrganisationName.Should().Be("Stub Reprocessing Ltd");
        result.Value.SubmitterContactDetails!.Email.Should().Be("stub.submitter@example.com");
        result.Value.Prns!.Authorisers.Should().OnlyContain(a => a.Email == "stub@example.com");
    }

    [Fact]
    public async Task GetNationAsync_TestOrganisation_ReturnsEngland()
    {
        var result = await NewSut()
            .GetNationAsync("e2e-c0ffee", "e2e-reg-c0ffee", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value.Should().Be(Nation.England);
    }

    [Fact]
    public async Task GetOrganisationNumberAsync_TestOrganisation_ReturnsAStableSixDigitNumber()
    {
        var sut = NewSut();

        var first = await sut.GetOrganisationNumberAsync(
            "e2e-c0ffee",
            TestContext.Current.CancellationToken
        );
        var again = await sut.GetOrganisationNumberAsync(
            "e2e-c0ffee",
            TestContext.Current.CancellationToken
        );

        first.IsSuccess.Should().BeTrue(because: first.Error?.Message);
        first.Value.Should().BeInRange(900_000, 999_999);
        again.Value.Should().Be(first.Value);
    }

    // ---------------- RA-690: derived registration reference ----------------

    // R + YY + agency {E|S|W|N} + {R|X} + 6-digit OrgId + 4-digit sequence + material code.
    private const string RegistrationReferenceFormat =
        "^R[0-9]{2}[ESWN][RX][0-9]{6}[0-9]{4}(ST|WO|AL|FB|PA|PL|GR|GO)$";

    [Fact]
    public async Task GetAccreditationAsync_TestOrganisation_ReturnsAValidRegistrationReference()
    {
        var result = await NewSut()
            .GetAccreditationAsync("e2e-c0ffee", "e2e-reg-c0ffee", MaterialType.Plastic, 2025);

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.RegistrationReference.Should().MatchRegex(RegistrationReferenceFormat);
        result
            .Value.RegistrationReference.Should()
            .StartWith(
                "R25ER",
                because: "YY is the year requested from ReEx, and a test organisation is an English reprocessor"
            )
            .And.EndWith("PL");
    }

    // Pinned literal, computed independently of the adapter (FNV-1a of the org and registration
    // ids): proves the reference is stable across processes and restarts, not just within one
    // run, so re-seeding the same registration always gives the same reference.
    [Fact]
    public async Task GetAccreditationAsync_TestOrganisation_RegistrationReferenceIsStable()
    {
        var first = await NewSut()
            .GetAccreditationAsync("e2e-c0ffee", "e2e-reg-c0ffee", MaterialType.Plastic, 2025);
        var again = await NewSut()
            .GetAccreditationAsync("e2e-c0ffee", "e2e-reg-c0ffee", MaterialType.Plastic, 2025);

        first.Value!.RegistrationReference.Should().Be("R25ER9730739108PL");
        again.Value!.RegistrationReference.Should().Be(first.Value.RegistrationReference);
    }

    [Fact]
    public async Task GetAccreditationAsync_DifferentRegistrationIds_GiveDifferentRegistrationReferences()
    {
        var sut = NewSut();

        var a = await sut.GetAccreditationAsync(
            "e2e-c0ffee",
            "e2e-reg-a",
            MaterialType.Plastic,
            2025
        );
        var b = await sut.GetAccreditationAsync(
            "e2e-c0ffee",
            "e2e-reg-b",
            MaterialType.Plastic,
            2025
        );

        a.Value!.RegistrationReference.Should().MatchRegex(RegistrationReferenceFormat);
        b.Value!.RegistrationReference.Should().NotBe(a.Value.RegistrationReference);
    }

    [Theory]
    [InlineData("60001", "R26ER060001")] // ea
    [InlineData("60002", "R26SR060002")] // sepa
    [InlineData("60003", "R26WR060003")] // nrw
    [InlineData("60004", "R26NR060004")] // niea
    [InlineData("50005", "R26EX050005")] // exporter, no regulator code -> England
    public async Task GetAccreditationAsync_NumericFakeOrganisation_ReferenceUsesItsNationProcessTypeAndOrgNumber(
        string organisationId,
        string expectedPrefix
    )
    {
        var org = await new FakeOrganisationPersistence().GetByOrgIdAsync(
            int.Parse(organisationId, System.Globalization.CultureInfo.InvariantCulture)
        );

        var result = await NewSut()
            .GetAccreditationAsync(
                organisationId,
                org!.Registrations![0].Id.ToString(),
                MaterialType.Plastic,
                2026
            );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result
            .Value!.RegistrationReference.Should()
            .MatchRegex(RegistrationReferenceFormat)
            .And.StartWith(expectedPrefix);
    }

    [Theory]
    [InlineData(MaterialType.Steel, "ST")]
    [InlineData(MaterialType.Wood, "WO")]
    [InlineData(MaterialType.Aluminium, "AL")]
    [InlineData(MaterialType.Fibre, "FB")]
    [InlineData(MaterialType.Paper, "PA")]
    [InlineData(MaterialType.Plastic, "PL")]
    public async Task GetAccreditationAsync_RegistrationReferenceEndsWithTheMaterialCode(
        MaterialType materialType,
        string expectedCode
    )
    {
        var result = await NewSut()
            .GetAccreditationAsync("e2e-c0ffee", "e2e-reg-c0ffee", materialType, 2025);

        result
            .Value!.RegistrationReference.Should()
            .MatchRegex(RegistrationReferenceFormat)
            .And.EndWith(expectedCode);
    }

    [Theory]
    [InlineData("glass_re_melt", "GR")]
    [InlineData("glass_other", "GO")]
    [InlineData("glass_pulverise", "GO")] // unrecognised: no process, so "other"
    public async Task GetAccreditationAsync_Glass_RegistrationReferenceReflectsTheRecyclingProcess(
        string glassRecyclingProcess,
        string expectedCode
    )
    {
        var fakeOrgs = new FakeOrganisationPersistence();
        var registrationId = ObjectId.GenerateNewId();
        await fakeOrgs.CreateAsync(
            new OrganisationModel
            {
                OrgId = 70003,
                Registrations =
                [
                    new RegistrationModel
                    {
                        Id = registrationId,
                        Status = "created",
                        Material = "glass",
                        GlassRecyclingProcess = glassRecyclingProcess,
                        WasteProcessingType = "reprocessor",
                    },
                ],
            }
        );
        var sut = new StubReExApiAdapter(fakeOrgs, EnabledNullLogger<StubReExApiAdapter>.Instance);

        var result = await sut.GetAccreditationAsync(
            "70003",
            registrationId.ToString(),
            MaterialType.Glass,
            2026
        );

        result
            .Value!.RegistrationReference.Should()
            .MatchRegex(RegistrationReferenceFormat)
            .And.StartWith("R26ER070003")
            .And.EndWith(expectedCode);
    }

    // An id with no usable 6-digit org number - a UUID (what a real ReEx organisation id is),
    // a number too long for the OrgId segment, or a signed one - is hashed instead.
    [Theory]
    [InlineData("c14854d9-e20a-41b3-87d5-a1fd4bbe2153", "R26ER024451")]
    [InlineData("1234567", "R26ER378663")]
    [InlineData("-5", "R26ER092279")]
    public async Task GetAccreditationAsync_NoUsableOrgNumber_ReferenceUsesAStableHashOfTheId(
        string organisationId,
        string expectedPrefix
    )
    {
        var result = await NewSut()
            .GetAccreditationAsync(organisationId, "reg-1", MaterialType.Plastic, 2026);

        result
            .Value!.RegistrationReference.Should()
            .MatchRegex(RegistrationReferenceFormat)
            .And.StartWith(expectedPrefix);
    }

    [Fact]
    public void TestOrganisationNumber_DiffersBetweenTestOrganisations()
    {
        StubReExApiAdapter
            .TestOrganisationNumber("e2e-c0ffee")
            .Should()
            .NotBe(StubReExApiAdapter.TestOrganisationNumber("e2e-decaf0"));
    }
}
