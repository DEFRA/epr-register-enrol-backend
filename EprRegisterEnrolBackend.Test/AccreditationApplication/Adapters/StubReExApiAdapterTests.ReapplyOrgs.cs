using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

// RA-765: seeding a reapply org's new application copies the accreditation it was last granted, so the stub ReEx
// must hold that year's: 2025 for 50026, 2026 for 50027 and 50028, and 2024 for 50029 (lapsed), when the clock
// says 2026.
public partial class StubReExApiAdapterTests
{
    [Theory]
    [InlineData(50026, 2025, "Reapply Current Year Ltd")]
    [InlineData(50027, 2026, "Applied Next Year Ltd")]
    [InlineData(50028, 2026, "Refused Next Year Ltd")]
    [InlineData(50029, 2024, "Lapsed Accreditation Ltd")]
    public async Task GetAccreditationAsync_ReapplyOrg_HoldsTheYearItWasGranted(
        int orgId,
        int grantedYear,
        string organisationName
    )
    {
        var fakeOrgs = new FakeOrganisationPersistence(
            new TestClock(new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc)),
            includeDevExporters: true
        );
        var sut = new StubReExApiAdapter(fakeOrgs, EnabledNullLogger<StubReExApiAdapter>.Instance);

        var result = await sut.GetAccreditationAsync(
            orgId.ToString(),
            $"aaa{orgId:D21}",
            MaterialType.Plastic,
            grantedYear
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.Year.Should().Be(grantedYear);
        result.Value.OrganisationName.Should().Be(organisationName);
        result.Value.IsExporter.Should().BeTrue();
        result.Value.RegistrationStatus.Should().Be(RegistrationStatus.Approved);
        result.Value.AccreditationNumber.Should().StartWith($"A{grantedYear % 100:D2}EX0{orgId}");
    }

    // RA-765: following the manage page's apply link seeds an application, which refreshes the registration from
    // ReEx. The stub must report the numbers the dev seed gave the registration and that year's accreditation, or
    // the refresh replaces the registration number and the manage page, found by that number, is a 404.
    [Theory]
    [InlineData(50018, "aaa000000000000000050018", MaterialType.Plastic, 2026, "R26EX0500180001PL", "A26EX0500180001PL")]
    [InlineData(50018, "bbb000000000000000050018", MaterialType.Glass, 2026, "R26ER0500180002GR", "A26ER0500180002GR")]
    [InlineData(50026, "aaa000000000000000050026", MaterialType.Plastic, 2025, "R26EX0500260001PL", "A25EX0500260001PL")]
    [InlineData(50029, "aaa000000000000000050029", MaterialType.Plastic, 2024, "R26EX0500290001PL", "A24EX0500290001PL")]
    public async Task GetAccreditationAsync_DevExporterRegistration_ReportsTheDevSeedsNumbers(
        int orgId,
        string registrationId,
        MaterialType materialType,
        int year,
        string registrationNumber,
        string accreditationNumber
    )
    {
        var fakeOrgs = new FakeOrganisationPersistence(
            new TestClock(new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc)),
            includeDevExporters: true
        );
        var sut = new StubReExApiAdapter(fakeOrgs, EnabledNullLogger<StubReExApiAdapter>.Instance);

        var result = await sut.GetAccreditationAsync(orgId.ToString(), registrationId, materialType, year);

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.RegistrationReference.Should().Be(registrationNumber);
        result.Value.AccreditationNumber.Should().Be(accreditationNumber);
    }
}
