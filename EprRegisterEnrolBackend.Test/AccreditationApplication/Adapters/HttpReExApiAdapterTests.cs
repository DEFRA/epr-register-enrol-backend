using System.Net;
using System.Text;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.ReEx.Config;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

/// <summary>
/// Regression coverage for RA-334: HttpReExApiAdapter.GetAccreditationAsync used to source
/// RegistrationReference from org.CompanyDetails.RegistrationNumber, which the real ReEx API
/// never populates — the actual EPR registration number lives per-registration, under
/// registrations[].registrationNumber. Every other test that exercises accreditation submission
/// mocks IReExApiAdapter entirely, so nothing previously exercised this adapter's own mapping
/// logic against realistic ReEx JSON shape.
/// </summary>
public class HttpReExApiAdapterTests
{
    private static HttpReExApiAdapter BuildSut(
        string organisationJson,
        string overseasSitesJson = "{}",
        HttpStatusCode organisationStatusCode = HttpStatusCode.OK,
        HttpStatusCode overseasSitesStatusCode = HttpStatusCode.OK
    )
    {
        var handler = new RoutingHandler(
            organisationJson,
            overseasSitesJson,
            organisationStatusCode,
            overseasSitesStatusCode
        );
        var httpClient = new HttpClient(handler);
        var config = Options.Create(new ReExConfig { BaseUrl = "http://localhost:5000/" });
        var reExClient = new ReExClient(httpClient, config, EnabledNullLogger<ReExClient>.Instance);
        return new HttpReExApiAdapter(reExClient, EnabledNullLogger<HttpReExApiAdapter>.Instance);
    }

    [Fact]
    public async Task GetAccreditationAsync_ReprocessorRegistration_ReturnsRegistrationLevelRegistrationNumber()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.RegistrationReference.Should().Be("R25SR500000912AL");
    }

    [Fact]
    public async Task GetAccreditationAsync_ExporterRegistration_ReturnsRegistrationLevelRegistrationNumber()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.RegistrationReference.Should().Be("E25SR500020912AL");
    }

    [Fact]
    public async Task GetAccreditationAsync_ReprocessorRegistration_MapsWasteProcessingTypeAndPostcode()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.WasteProcessingType.Should().Be("reprocessor");
        result.Value!.CompanyRegisterAddressPostcode.Should().Be("AB1 2CD");
    }

    [Fact]
    public async Task GetAccreditationAsync_ExporterRegistration_MapsWasteProcessingTypeAndPostcode()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.WasteProcessingType.Should().Be("exporter");
        result.Value!.CompanyRegisterAddressPostcode.Should().Be("AB1 2CD");
    }

    // RA-444: exporters have no UK processing site, so SiteAddress must stay null — the
    // frontend's nation resolution falls back to England whenever it sees a populated
    // siteAddress with no postcode, so it must instead read CompanyRegisterAddressPostcode
    // (asserted above) for exporters. Pins the exact contract that was silently broken.
    [Fact]
    public async Task GetAccreditationAsync_ExporterRegistration_SiteAddressIsNull()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.SiteAddress.Should().BeNull();
        result.Value!.CompanyRegisterAddressPostcode.Should().NotBeNullOrEmpty();
    }

    // RA-424: the frontend shows this in place of the (non-existent) overseas site address on
    // the exporter's accreditation application header/landing page.
    [Fact]
    public async Task GetAccreditationAsync_ExporterRegistration_MapsCompanyRegisteredAddress()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.CompanyRegisteredAddress.Should().Be("1 Example Hill, Exampleton, AB1 2CD");
    }

    // RA-434: companiesHouseNumber lives on companyDetails and is org-wide, not per-registration.
    [Fact]
    public async Task GetAccreditationAsync_MapsCompaniesHouseNumber()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.CompaniesHouseNumber.Should().Be("09876543");
    }

    // RA-434: only the PermitNumber strings are extracted — a permit with no permitNumber (e.g.
    // a waste exemption) must be dropped rather than surfaced as a null/blank entry.
    [Fact]
    public async Task GetAccreditationAsync_MapsPermitNumbersFromRegistrationOnly()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.PermitNumbers.Should().BeEquivalentTo(["WML123456"]);
    }

    [Fact]
    public async Task GetAccreditationAsync_RegistrationWithNoPermits_ReturnsEmptyPermitNumbers()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.PermitNumbers.Should().BeEmpty();
    }

    // Regression test for RA-424: the real ReEx API sends "up_to_5000" (confirmed by commit
    // c5bdf46, which set this fixture's tonnageBand to "up_to_5000" against a captured
    // production payload), but TonnageBandMap only recognised "up_to_1000" — every real exporter
    // accreditation with this band silently dropped to a null PlannedTonnageBand.
    [Fact]
    public async Task GetAccreditationAsync_ExporterRegistration_MapsUpTo5000TonnageBand()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.Prns!.PlannedTonnageBand.Should().Be(PlannedTonnageBand.UpTo5000);
    }

    [Fact]
    public async Task GetAccreditationAsync_ExporterRegistration_MapsSeededSitesWithIsNewSiteFalse()
    {
        const string overseasSitesJson = """
            {
              "1": {
                "name": "Overseas Recycling Co",
                "country": "France",
                "address": { "line1": "1 Rue Example", "townOrCity": "Paris" }
              }
            }
            """;
        var sut = BuildSut(OrganisationJson, overseasSitesJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.OverseasSites.Should().ContainSingle();
        var site = result.Value!.OverseasSites[0];
        site.SiteId.Should().Be(1);
        site.SiteName.Should().Be("Overseas Recycling Co");
        site.IsNewSite.Should()
            .BeFalse(because: "RA-297: ReEx-seeded sites are the registry, not new sites");
    }

    [Fact]
    public async Task GetAccreditationAsync_ExporterRegistration_NoRegisteredOfficePostcode_FailsRatherThanSubmittingMalformedPayload()
    {
        var sut = BuildSut(OrganisationJsonNoCompanyPostcode);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task GetAccreditationAsync_ReprocessorRegistration_NoRegisteredOfficePostcode_StillSucceeds()
    {
        var sut = BuildSut(OrganisationJsonNoCompanyPostcode);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result
            .IsSuccess.Should()
            .BeTrue(
                because: "the registered-office postcode guard only applies to exporters — reprocessors derive their regulator postcode from the site address"
            );
    }

    [Fact]
    public async Task GetAccreditationAsync_NoGlassRecyclingProcessKey_MapsToNull()
    {
        // OrganisationJson's registrations carry no glassRecyclingProcess key at all, matching
        // non-glass materials in the real ReEx API.
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.GlassRecyclingProcess.Should().BeNull();
    }

    [Fact]
    public async Task GetAccreditationAsync_EmptyGlassRecyclingProcessArray_MapsToNull()
    {
        var sut = BuildSut(GlassOrganisationJson("[]"));

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Glass,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result
            .Value!.GlassRecyclingProcess.Should()
            .BeNull(because: "an empty array means no recycling process was specified");
    }

    [Fact]
    public async Task GetAccreditationAsync_GlassRecyclingProcessReMelt_MapsToRemeltEnum()
    {
        var sut = BuildSut(GlassOrganisationJson("""["glass_re_melt"]"""));

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Glass,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.GlassRecyclingProcess.Should().Be(GlassRecyclingProcess.Remelt);
    }

    [Fact]
    public async Task GetAccreditationAsync_GlassRecyclingProcessOther_MapsToOtherEnum()
    {
        var sut = BuildSut(GlassOrganisationJson("""["glass_other"]"""));

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Glass,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.GlassRecyclingProcess.Should().Be(GlassRecyclingProcess.Other);
    }

    [Fact]
    public async Task GetAccreditationAsync_MoreThanOneGlassRecyclingProcessElement_TakesFirstElement()
    {
        var sut = BuildSut(GlassOrganisationJson("""["glass_other", "glass_re_melt"]"""));

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Glass,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result
            .Value!.GlassRecyclingProcess.Should()
            .Be(
                GlassRecyclingProcess.Other,
                because: "ReEx should only ever send 0 or 1 elements, but the first element is used defensively if it ever sends more"
            );
    }

    [Fact]
    public async Task GetAccreditationAsync_UnrecognisedGlassRecyclingProcessValue_MapsToNull()
    {
        var sut = BuildSut(GlassOrganisationJson("""["glass_pulverise"]"""));

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Glass,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result
            .Value!.GlassRecyclingProcess.Should()
            .BeNull(
                because: "an unrecognised wire value should not fail the whole accreditation lookup"
            );
    }

    [Fact]
    public async Task GetAccreditationAsync_OrganisationNotFound_ReturnsNotFoundFailure()
    {
        var sut = BuildSut("{}", organisationStatusCode: HttpStatusCode.NotFound);

        var result = await sut.GetAccreditationAsync(
            "does-not-exist",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetAccreditationAsync_OrganisationServerError_ReturnsFailureWithUpstreamStatusCode()
    {
        var sut = BuildSut("{}", organisationStatusCode: HttpStatusCode.InternalServerError);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task GetAccreditationAsync_RegistrationIdNotFoundOnOrganisation_ReturnsNotFoundFailure()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-does-not-exist",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(404);
        result.Error!.Message.Should().Contain("reg-does-not-exist");
    }

    [Fact]
    public async Task GetAccreditationAsync_NoAccreditationMatchesAccreditationId_ReturnsNotFoundFailure()
    {
        var sut = BuildSut(OrganisationJsonNoMatchingAccreditation);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetAccreditationAsync_DuplicateAccreditationIds_ReturnsClientErrorFailure()
    {
        var sut = BuildSut(OrganisationJsonDuplicateAccreditations);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeFalse();
        result
            .StatusCode.Should()
            .Be(500, because: "duplicate accreditation IDs are a data integrity violation");
    }

    [Fact]
    public async Task GetAccreditationAsync_UnparseableValidFrom_ReturnsFailure()
    {
        var sut = BuildSut(OrganisationJsonBadValidFrom);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task GetAccreditationAsync_YearDoesNotMatchValidFrom_ReturnsNotFoundFailure()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2099
        );

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetAccreditationAsync_ExporterOverseasSitesCallFails_ReturnsFailureFromUpstream()
    {
        var sut = BuildSut(
            OrganisationJson,
            overseasSitesStatusCode: HttpStatusCode.InternalServerError
        );

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task GetAccreditationAsync_MapsBusinessPlanEntries()
    {
        var sut = BuildSut(OrganisationJsonWithBusinessPlan);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.BusinessPlan.Should().NotBeNull();
        result.Value!.BusinessPlan!.NewInfrastructurePercent.Should().Be(10);
        result.Value!.BusinessPlan!.PriceSupportPercent.Should().Be(20);
        result.Value!.BusinessPlan!.BusinessCollectionsPercent.Should().Be(5);
        result.Value!.BusinessPlan!.CommunicationsPercent.Should().Be(5);
        result.Value!.BusinessPlan!.NewMarketsPercent.Should().Be(5);
        result.Value!.BusinessPlan!.NewUsesPercent.Should().Be(5);
        result.Value!.BusinessPlan!.OtherPercent.Should().Be(50);
    }

    [Fact]
    public async Task GetAccreditationAsync_UnrecognisedBusinessPlanEntry_IsIgnored()
    {
        var sut = BuildSut(OrganisationJsonWithUnrecognisedBusinessPlanEntry);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.BusinessPlan!.NewInfrastructurePercent.Should().BeNull();
    }

    [Fact]
    public async Task GetAccreditationAsync_BusinessPlanEntryWithNullUsageDescription_IsSkipped()
    {
        var sut = BuildSut(OrganisationJsonWithNullBusinessPlanFields);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.BusinessPlan!.NewInfrastructurePercent.Should().BeNull();
    }

    [Fact]
    public async Task GetAccreditationAsync_NoPrnIssuance_MapsEmptyAuthorisersAndNullTonnageBand()
    {
        var sut = BuildSut(OrganisationJsonNoPrnIssuance);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.Prns!.PlannedTonnageBand.Should().BeNull();
        result.Value!.Prns!.Authorisers.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAccreditationAsync_UnrecognisedTonnageBand_MapsToNull()
    {
        var sut = BuildSut(OrganisationJsonUnrecognisedTonnageBand);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.Prns!.PlannedTonnageBand.Should().BeNull();
    }

    // Covers FormatAddress(SiteAddressDto?) returning null when a reprocessor registration has
    // no "site" key at all — the ternary's null branch was previously untested (every other
    // fixture supplies a site).
    [Fact]
    public async Task GetAccreditationAsync_ReprocessorRegistrationWithNoSite_SiteAddressIsNull()
    {
        var sut = BuildSut(OrganisationJsonReprocessorNoSite);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result
            .Value!.SiteAddress.Should()
            .BeNull(because: "FormatAddress must handle a missing site gracefully");
    }

    // Covers FormatAddress(RegisteredAddressDto?) returning null when companyDetails has no
    // "address" key at all (reprocessor, so the exporter-only postcode guard doesn't apply).
    [Fact]
    public async Task GetAccreditationAsync_NoCompanyAddress_MapsNullRegisteredAddressAndPostcode()
    {
        var sut = BuildSut(OrganisationJsonNoCompanyAddress);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.CompanyRegisteredAddress.Should().BeNull();
        result.Value!.CompanyRegisterAddressPostcode.Should().BeNull();
    }

    // Covers MapOverseasSite's two defensive ternaries: a non-numeric overseas-site dictionary
    // key falls back to SiteId 0 rather than throwing, and a site with no "address" key maps to
    // a null SiteAddress rather than throwing.
    [Fact]
    public async Task GetAccreditationAsync_ExporterOverseasSite_NonNumericKeyAndNoAddress_FallsBackToDefaults()
    {
        const string overseasSitesJson = """
            {
              "site-A": {
                "name": "Overseas Recycling Co",
                "country": "France"
              }
            }
            """;
        var sut = BuildSut(OrganisationJson, overseasSitesJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.OverseasSites.Should().ContainSingle();
        var site = result.Value!.OverseasSites[0];
        site.SiteId.Should()
            .Be(0, because: "a non-numeric overseas site key has no id to fall back to");
        site.SiteAddress.Should().BeNull(because: "the site had no address in the ReEx payload");
    }

    [Fact]
    public async Task GetLinkedDefraOrganisationAsync_Success_ReturnsLinkedOrgId()
    {
        var sut = BuildSut(OrganisationJsonWithLinkedDefraOrganisation);

        var result = await sut.GetLinkedDefraOrganisationAsync(
            "6a2fcd74e16883c137d01188",
            TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.OrganisationId.Should().Be("6a2fcd74e16883c137d01188");
        result.Value!.LinkedDefraOrganisationId.Should().Be("defra-org-123");
    }

    [Fact]
    public async Task GetLinkedDefraOrganisationAsync_NoLinkedOrganisation_ReturnsSuccessWithNullId()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetLinkedDefraOrganisationAsync(
            "6a2fcd74e16883c137d01188",
            TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.LinkedDefraOrganisationId.Should().BeNull();
    }

    [Fact]
    public async Task GetLinkedDefraOrganisationAsync_OrganisationNotFound_ReturnsNotFoundFailure()
    {
        var sut = BuildSut("{}", organisationStatusCode: HttpStatusCode.NotFound);

        var result = await sut.GetLinkedDefraOrganisationAsync(
            "does-not-exist",
            TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetLinkedDefraOrganisationAsync_OrganisationServerError_ReturnsFailure()
    {
        var sut = BuildSut("{}", organisationStatusCode: HttpStatusCode.InternalServerError);

        var result = await sut.GetLinkedDefraOrganisationAsync(
            "6a2fcd74e16883c137d01188",
            TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(500);
    }

    // ---------------- RA-475: GetOrganisationNumberAsync ----------------

    [Fact]
    public async Task GetOrganisationNumberAsync_Success_ReturnsOrgId()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetOrganisationNumberAsync(
            "6a2fcd74e16883c137d01188",
            TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value.Should().Be(509193);
    }

    [Fact]
    public async Task GetOrganisationNumberAsync_NoOrgIdRecorded_ReturnsSuccessWithNullValue()
    {
        var sut = BuildSut("{}");

        var result = await sut.GetOrganisationNumberAsync(
            "6a2fcd74e16883c137d01188",
            TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetOrganisationNumberAsync_OrganisationNotFound_ReturnsNotFoundFailure()
    {
        var sut = BuildSut("{}", organisationStatusCode: HttpStatusCode.NotFound);

        var result = await sut.GetOrganisationNumberAsync(
            "does-not-exist",
            TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetOrganisationNumberAsync_OrganisationServerError_ReturnsFailure()
    {
        var sut = BuildSut("{}", organisationStatusCode: HttpStatusCode.InternalServerError);

        var result = await sut.GetOrganisationNumberAsync(
            "6a2fcd74e16883c137d01188",
            TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(500);
    }

    // Realistic redacted ReEx organisation payload — companyDetails deliberately has no
    // registrationNumber key, matching the real API. Mirrors the fixture used in
    // ReExOrganisationFixtureTests.cs.
    private const string OrganisationJson = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor", "exporter"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "tradingName": "Test Recycling Solutions Ltd",
            "companiesHouseNumber": "09876543",
            "address": {
              "line1": "1 Example Hill",
              "postcode": "AB1 2CD",
              "country": "UK",
              "town": "Exampleton"
            }
          },
          "submittedToRegulator": "ea",
          "registrations": [
            {
              "id": "reg-reprocessor-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "site": {
                "address": {
                  "line1": "Reprocessor Site Road",
                  "postcode": "HU7 7BX",
                  "country": "UK",
                  "town": "Exampleton"
                },
                "gridReference": "TQ 132 546"
              },
              "cbduNumber": "CBDU663848",
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "wasteManagementPermits": [
                { "type": "environmental_permit", "permitNumber": "WML123456" },
                { "type": "waste_exemption" }
              ],
              "accreditationId": "acc-reprocessor-1",
              "registrationNumber": "R25SR500000912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "reprocessingType": "input",
              "status": "approved",
              "accreditation": null
            },
            {
              "id": "reg-exporter-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "noticeAddress": {
                "fullAddress": "1 Example Parade, Example Town",
                "country": "UK"
              },
              "cbduNumber": "CBDU506923",
              "material": "aluminium",
              "exportPorts": ["Southampton", "Portsmouth"],
              "wasteProcessingType": "exporter",
              "accreditationId": "acc-exporter-1",
              "registrationNumber": "E25SR500020912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "status": "approved",
              "accreditation": null
            }
          ],
          "accreditations": [
            {
              "id": "acc-reprocessor-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "reprocessor",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": {
                "tonnageBand": "over_10000",
                "signatories": [
                  { "fullName": "Test Signatory", "email": "signatory@example.test", "phone": "0111 000 0002", "jobTitle": "Director" }
                ],
                "incomeBusinessPlan": []
              },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "R-ACC12045AL",
              "reprocessingType": "input",
              "status": "approved"
            },
            {
              "id": "acc-exporter-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "exporter",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": {
                "tonnageBand": "up_to_5000",
                "signatories": [
                  { "fullName": "Test Exporter Signatory", "email": "exporter.signatory@example.test", "phone": "1234567890", "jobTitle": "Director" }
                ],
                "incomeBusinessPlan": []
              },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "E-ACC12245AL",
              "status": "approved"
            }
          ]
        }
        """;

    // Same as OrganisationJson but companyDetails.address has no postcode key, reproducing
    // the malformed-upstream-data shape from PR review comment
    // DEFRA/epr-register-enrol-backend#64.
    private const string OrganisationJsonNoCompanyPostcode = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor", "exporter"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "tradingName": "Test Recycling Solutions Ltd",
            "address": {
              "line1": "1 Example Hill",
              "country": "UK",
              "town": "Exampleton"
            }
          },
          "submittedToRegulator": "ea",
          "registrations": [
            {
              "id": "reg-reprocessor-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "site": {
                "address": {
                  "line1": "Reprocessor Site Road",
                  "postcode": "HU7 7BX",
                  "country": "UK",
                  "town": "Exampleton"
                },
                "gridReference": "TQ 132 546"
              },
              "cbduNumber": "CBDU663848",
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "accreditationId": "acc-reprocessor-1",
              "registrationNumber": "R25SR500000912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "reprocessingType": "input",
              "status": "approved",
              "accreditation": null
            },
            {
              "id": "reg-exporter-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "noticeAddress": {
                "fullAddress": "1 Example Parade, Example Town",
                "country": "UK"
              },
              "cbduNumber": "CBDU506923",
              "material": "aluminium",
              "exportPorts": ["Southampton", "Portsmouth"],
              "wasteProcessingType": "exporter",
              "accreditationId": "acc-exporter-1",
              "registrationNumber": "E25SR500020912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "status": "approved",
              "accreditation": null
            }
          ],
          "accreditations": [
            {
              "id": "acc-reprocessor-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "reprocessor",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": {
                "tonnageBand": "over_10000",
                "signatories": [
                  { "fullName": "Test Signatory", "email": "signatory@example.test", "phone": "0111 000 0002", "jobTitle": "Director" }
                ],
                "incomeBusinessPlan": []
              },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "R-ACC12045AL",
              "reprocessingType": "input",
              "status": "approved"
            },
            {
              "id": "acc-exporter-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "exporter",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": {
                "tonnageBand": "up_to_5000",
                "signatories": [
                  { "fullName": "Test Exporter Signatory", "email": "exporter.signatory@example.test", "phone": "1234567890", "jobTitle": "Director" }
                ],
                "incomeBusinessPlan": []
              },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "E-ACC12245AL",
              "status": "approved"
            }
          ]
        }
        """;

    // Minimal single-registration glass organisation payload, with the glassRecyclingProcess
    // array substituted in — used to cover RA-307's array-parsing/enum-mapping cases without
    // duplicating the full dual-registration OrganisationJson fixture above.
    private static string GlassOrganisationJson(string glassRecyclingProcessArrayJson) =>
        $$"""
            {
              "id": "6a2fcd74e16883c137d01188",
              "schemaVersion": 3,
              "orgId": 509193,
              "wasteProcessingTypes": ["reprocessor"],
              "reprocessingNations": ["england"],
              "businessType": "individual",
              "companyDetails": {
                "name": "Test Glass Recycling Ltd",
                "tradingName": "Test Glass Recycling Ltd",
                "address": {
                  "line1": "1 Example Hill",
                  "postcode": "AB1 2CD",
                  "country": "UK",
                  "town": "Exampleton"
                }
              },
              "submittedToRegulator": "ea",
              "registrations": [
                {
                  "id": "reg-reprocessor-1",
                  "submittedToRegulator": "ea",
                  "orgName": "Test Glass Recycling Ltd",
                  "site": {
                    "address": {
                      "line1": "Reprocessor Site Road",
                      "postcode": "HU7 7BX",
                      "country": "UK",
                      "town": "Exampleton"
                    },
                    "gridReference": "TQ 132 546"
                  },
                  "cbduNumber": "CBDU663848",
                  "material": "glass",
                  "wasteProcessingType": "reprocessor",
                  "accreditationId": "acc-reprocessor-1",
                  "registrationNumber": "R25SR500000912GL",
                  "validFrom": "2026-01-01",
                  "validTo": "2027-01-01",
                  "reprocessingType": "input",
                  "status": "approved",
                  "glassRecyclingProcess": {{glassRecyclingProcessArrayJson}},
                  "accreditation": null
                }
              ],
              "accreditations": [
                {
                  "id": "acc-reprocessor-1",
                  "submittedToRegulator": "ea",
                  "wasteProcessingType": "reprocessor",
                  "material": "glass",
                  "orgName": "Test Glass Recycling Ltd",
                  "prnIssuance": {
                    "tonnageBand": "over_10000",
                    "signatories": [
                      { "fullName": "Test Signatory", "email": "signatory@example.test", "phone": "0111 000 0002", "jobTitle": "Director" }
                    ],
                    "incomeBusinessPlan": []
                  },
                  "validFrom": "2026-01-01",
                  "validTo": "2027-01-01",
                  "accreditationNumber": "R-ACC12045GL",
                  "reprocessingType": "input",
                  "status": "approved"
                }
              ]
            }
            """;

    // Same as OrganisationJson's reg-reprocessor-1 registration but with no "site" key at all,
    // covering FormatAddress(SiteAddressDto?)'s null branch.
    private const string OrganisationJsonReprocessorNoSite = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "address": { "line1": "1 Example Hill", "postcode": "AB1 2CD", "country": "UK", "town": "Exampleton" }
          },
          "submittedToRegulator": "ea",
          "registrations": [
            {
              "id": "reg-reprocessor-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "accreditationId": "acc-reprocessor-1",
              "registrationNumber": "R25SR500000912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "reprocessingType": "input",
              "status": "approved",
              "accreditation": null
            }
          ],
          "accreditations": [
            {
              "id": "acc-reprocessor-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "reprocessor",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": { "tonnageBand": "over_10000", "signatories": [], "incomeBusinessPlan": [] },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "R-ACC12045AL",
              "reprocessingType": "input",
              "status": "approved"
            }
          ]
        }
        """;

    // Same as OrganisationJsonReprocessorNoSite but companyDetails has no "address" key at all,
    // covering FormatAddress(RegisteredAddressDto?)'s null branch.
    private const string OrganisationJsonNoCompanyAddress = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd"
          },
          "submittedToRegulator": "ea",
          "registrations": [
            {
              "id": "reg-reprocessor-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "site": {
                "address": {
                  "line1": "Reprocessor Site Road",
                  "postcode": "HU7 7BX",
                  "country": "UK",
                  "town": "Exampleton"
                }
              },
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "accreditationId": "acc-reprocessor-1",
              "registrationNumber": "R25SR500000912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "reprocessingType": "input",
              "status": "approved",
              "accreditation": null
            }
          ],
          "accreditations": [
            {
              "id": "acc-reprocessor-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "reprocessor",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": { "tonnageBand": "over_10000", "signatories": [], "incomeBusinessPlan": [] },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "R-ACC12045AL",
              "reprocessingType": "input",
              "status": "approved"
            }
          ]
        }
        """;

    private const string OrganisationJsonNoMatchingAccreditation = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "address": { "line1": "1 Example Hill", "postcode": "AB1 2CD", "country": "UK", "town": "Exampleton" }
          },
          "submittedToRegulator": "ea",
          "registrations": [
            {
              "id": "reg-reprocessor-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "site": { "address": { "line1": "Reprocessor Site Road", "postcode": "HU7 7BX", "country": "UK", "town": "Exampleton" } },
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "accreditationId": "acc-reprocessor-1",
              "registrationNumber": "R25SR500000912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "reprocessingType": "input",
              "status": "approved",
              "accreditation": null
            }
          ],
          "accreditations": []
        }
        """;

    private const string OrganisationJsonDuplicateAccreditations = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "address": { "line1": "1 Example Hill", "postcode": "AB1 2CD", "country": "UK", "town": "Exampleton" }
          },
          "submittedToRegulator": "ea",
          "registrations": [
            {
              "id": "reg-reprocessor-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "site": { "address": { "line1": "Reprocessor Site Road", "postcode": "HU7 7BX", "country": "UK", "town": "Exampleton" } },
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "accreditationId": "acc-reprocessor-1",
              "registrationNumber": "R25SR500000912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "reprocessingType": "input",
              "status": "approved",
              "accreditation": null
            }
          ],
          "accreditations": [
            {
              "id": "acc-reprocessor-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "reprocessor",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": { "tonnageBand": "over_10000", "signatories": [], "incomeBusinessPlan": [] },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "R-ACC12045AL",
              "reprocessingType": "input",
              "status": "approved"
            },
            {
              "id": "acc-reprocessor-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "reprocessor",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": { "tonnageBand": "over_10000", "signatories": [], "incomeBusinessPlan": [] },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "R-ACC12046AL",
              "reprocessingType": "input",
              "status": "approved"
            }
          ]
        }
        """;

    private const string OrganisationJsonBadValidFrom = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "address": { "line1": "1 Example Hill", "postcode": "AB1 2CD", "country": "UK", "town": "Exampleton" }
          },
          "submittedToRegulator": "ea",
          "registrations": [
            {
              "id": "reg-reprocessor-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "site": { "address": { "line1": "Reprocessor Site Road", "postcode": "HU7 7BX", "country": "UK", "town": "Exampleton" } },
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "accreditationId": "acc-reprocessor-1",
              "registrationNumber": "R25SR500000912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "reprocessingType": "input",
              "status": "approved",
              "accreditation": null
            }
          ],
          "accreditations": [
            {
              "id": "acc-reprocessor-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "reprocessor",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": { "tonnageBand": "over_10000", "signatories": [], "incomeBusinessPlan": [] },
              "validFrom": "not-a-date",
              "validTo": "2027-01-01",
              "accreditationNumber": "R-ACC12045AL",
              "reprocessingType": "input",
              "status": "approved"
            }
          ]
        }
        """;

    private const string OrganisationJsonWithBusinessPlan = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "address": { "line1": "1 Example Hill", "postcode": "AB1 2CD", "country": "UK", "town": "Exampleton" }
          },
          "submittedToRegulator": "ea",
          "registrations": [
            {
              "id": "reg-reprocessor-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "site": { "address": { "line1": "Reprocessor Site Road", "postcode": "HU7 7BX", "country": "UK", "town": "Exampleton" } },
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "accreditationId": "acc-reprocessor-1",
              "registrationNumber": "R25SR500000912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "reprocessingType": "input",
              "status": "approved",
              "accreditation": null
            }
          ],
          "accreditations": [
            {
              "id": "acc-reprocessor-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "reprocessor",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": {
                "tonnageBand": "over_10000",
                "signatories": [],
                "incomeBusinessPlan": [
                  { "usageDescription": "New reprocessing infrastructure and maintaining existing infrastructure", "percentIncomeSpent": 10 },
                  { "usageDescription": "Price support for buying packaging waste or selling recycled packaging waste", "percentIncomeSpent": 20 },
                  { "usageDescription": "Support for business collections", "percentIncomeSpent": 5 },
                  { "usageDescription": "Communications, including information campaigns", "percentIncomeSpent": 5 },
                  { "usageDescription": "Developing new markets for products made from recycled packaging waste", "percentIncomeSpent": 5 },
                  { "usageDescription": "Developing new uses for recycled packaging waste", "percentIncomeSpent": 5 },
                  { "usageDescription": "Activities or investment not covered by the other categories", "percentIncomeSpent": 50 }
                ]
              },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "R-ACC12045AL",
              "reprocessingType": "input",
              "status": "approved"
            }
          ]
        }
        """;

    private const string OrganisationJsonWithUnrecognisedBusinessPlanEntry = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "address": { "line1": "1 Example Hill", "postcode": "AB1 2CD", "country": "UK", "town": "Exampleton" }
          },
          "submittedToRegulator": "ea",
          "registrations": [
            {
              "id": "reg-reprocessor-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "site": { "address": { "line1": "Reprocessor Site Road", "postcode": "HU7 7BX", "country": "UK", "town": "Exampleton" } },
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "accreditationId": "acc-reprocessor-1",
              "registrationNumber": "R25SR500000912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "reprocessingType": "input",
              "status": "approved",
              "accreditation": null
            }
          ],
          "accreditations": [
            {
              "id": "acc-reprocessor-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "reprocessor",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": {
                "tonnageBand": "over_10000",
                "signatories": [],
                "incomeBusinessPlan": [
                  { "usageDescription": "Something ReEx invents later", "percentIncomeSpent": 99 }
                ]
              },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "R-ACC12045AL",
              "reprocessingType": "input",
              "status": "approved"
            }
          ]
        }
        """;

    private const string OrganisationJsonWithNullBusinessPlanFields = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "address": { "line1": "1 Example Hill", "postcode": "AB1 2CD", "country": "UK", "town": "Exampleton" }
          },
          "submittedToRegulator": "ea",
          "registrations": [
            {
              "id": "reg-reprocessor-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "site": { "address": { "line1": "Reprocessor Site Road", "postcode": "HU7 7BX", "country": "UK", "town": "Exampleton" } },
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "accreditationId": "acc-reprocessor-1",
              "registrationNumber": "R25SR500000912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "reprocessingType": "input",
              "status": "approved",
              "accreditation": null
            }
          ],
          "accreditations": [
            {
              "id": "acc-reprocessor-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "reprocessor",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": {
                "tonnageBand": "over_10000",
                "signatories": [],
                "incomeBusinessPlan": [
                  { "usageDescription": null, "percentIncomeSpent": 10 },
                  { "usageDescription": "New reprocessing infrastructure and maintaining existing infrastructure", "percentIncomeSpent": null }
                ]
              },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "R-ACC12045AL",
              "reprocessingType": "input",
              "status": "approved"
            }
          ]
        }
        """;

    private const string OrganisationJsonNoPrnIssuance = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "address": { "line1": "1 Example Hill", "postcode": "AB1 2CD", "country": "UK", "town": "Exampleton" }
          },
          "submittedToRegulator": "ea",
          "registrations": [
            {
              "id": "reg-reprocessor-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "site": { "address": { "line1": "Reprocessor Site Road", "postcode": "HU7 7BX", "country": "UK", "town": "Exampleton" } },
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "accreditationId": "acc-reprocessor-1",
              "registrationNumber": "R25SR500000912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "reprocessingType": "input",
              "status": "approved",
              "accreditation": null
            }
          ],
          "accreditations": [
            {
              "id": "acc-reprocessor-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "reprocessor",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "R-ACC12045AL",
              "reprocessingType": "input",
              "status": "approved"
            }
          ]
        }
        """;

    private const string OrganisationJsonUnrecognisedTonnageBand = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "address": { "line1": "1 Example Hill", "postcode": "AB1 2CD", "country": "UK", "town": "Exampleton" }
          },
          "submittedToRegulator": "ea",
          "registrations": [
            {
              "id": "reg-reprocessor-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "site": { "address": { "line1": "Reprocessor Site Road", "postcode": "HU7 7BX", "country": "UK", "town": "Exampleton" } },
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "accreditationId": "acc-reprocessor-1",
              "registrationNumber": "R25SR500000912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "reprocessingType": "input",
              "status": "approved",
              "accreditation": null
            }
          ],
          "accreditations": [
            {
              "id": "acc-reprocessor-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "reprocessor",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": { "tonnageBand": "up_to_a_million", "signatories": [], "incomeBusinessPlan": [] },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "R-ACC12045AL",
              "reprocessingType": "input",
              "status": "approved"
            }
          ]
        }
        """;

    private const string OrganisationJsonWithLinkedDefraOrganisation = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "address": { "line1": "1 Example Hill", "postcode": "AB1 2CD", "country": "UK", "town": "Exampleton" }
          },
          "submittedToRegulator": "ea",
          "linkedDefraOrganisation": { "orgId": "defra-org-123" },
          "registrations": [],
          "accreditations": []
        }
        """;

    // Returns the organisation payload for the organisations endpoint, and an empty
    // overseas-sites dictionary for the overseas-sites endpoint the adapter calls for
    // exporter registrations — a single fixed body can't serve both shapes.
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly string _organisationJson;
        private readonly string _overseasSitesJson;
        private readonly HttpStatusCode _organisationStatusCode;
        private readonly HttpStatusCode _overseasSitesStatusCode;

        public RoutingHandler(
            string organisationJson,
            string overseasSitesJson = "{}",
            HttpStatusCode organisationStatusCode = HttpStatusCode.OK,
            HttpStatusCode overseasSitesStatusCode = HttpStatusCode.OK
        )
        {
            _organisationJson = organisationJson;
            _overseasSitesJson = overseasSitesJson;
            _organisationStatusCode = organisationStatusCode;
            _overseasSitesStatusCode = overseasSitesStatusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var isOverseasSites = request.RequestUri!.AbsolutePath.Contains("overseas-sites");
            var body = isOverseasSites ? _overseasSitesJson : _organisationJson;
            var statusCode = isOverseasSites ? _overseasSitesStatusCode : _organisationStatusCode;

            return Task.FromResult(
                new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
        }
    }
}
