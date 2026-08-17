using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Organisation.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

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
            NullLogger<StubReExApiAdapter>.Instance
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
            NullLogger<StubReExApiAdapter>.Instance
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
            NullLogger<StubReExApiAdapter>.Instance
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
            NullLogger<StubReExApiAdapter>.Instance
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
}
