using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Organisation.Models;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

// RA-806: the stub carries its fixture registration's ReEx status, and fails on an unknown one, as
// HttpReExApiAdapter does for ReEx's.
public partial class StubReExApiAdapterTests
{
    private static Task<ReExResult<ReExAccreditationDto>> StubLookupAsync(
        string organisationId,
        string registrationId,
        FakeOrganisationPersistence? fakeOrgs = null,
        CapturingLogger<StubReExApiAdapter>? logger = null
    ) =>
        new StubReExApiAdapter(
            fakeOrgs ?? new FakeOrganisationPersistence(),
            logger ?? (ILogger<StubReExApiAdapter>)EnabledNullLogger<StubReExApiAdapter>.Instance
        ).GetAccreditationAsync(organisationId, registrationId, MaterialType.Plastic, 2026);

    private static async Task<ReExAccreditationDto> StubAccreditationAsync(
        string organisationId,
        string registrationId
    )
    {
        var result = await StubLookupAsync(organisationId, registrationId);
        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        return result.Value!;
    }

    [Fact]
    public async Task GetAccreditationAsync_ApprovedFixtureRegistration_IsApproved()
    {
        var dto = await StubAccreditationAsync("50023", FakeOrganisationPersistence.Reg50023.ToString());

        dto.RegistrationStatus.Should().Be(RegistrationStatus.Approved);
    }

    [Fact]
    public async Task GetAccreditationAsync_CreatedFixtureRegistration_IsCreated()
    {
        var dto = await StubAccreditationAsync("50024", FakeOrganisationPersistence.Reg50024.ToString());

        dto.RegistrationStatus.Should().Be(RegistrationStatus.Created);
    }

    // Every other fixture registration holds or applies for an accreditation, so is approved in ReEx: seeding one
    // must not take a dev-seeded Approved registration back to Created.
    [Fact]
    public async Task GetAccreditationAsync_ExistingFixtureRegistration_IsApproved()
    {
        var dto = await StubAccreditationAsync("50005", FakeOrganisationPersistence.Reg50005.ToString());

        dto.RegistrationStatus.Should().Be(RegistrationStatus.Approved);
    }

    // An E2E test organisation has no fixture: the stub makes its registration up, approved.
    [Fact]
    public async Task GetAccreditationAsync_TestOrganisationWithoutFixture_IsApproved()
    {
        var dto = await StubAccreditationAsync("e2e-ra806", ObjectId.GenerateNewId().ToString());

        dto.RegistrationStatus.Should().Be(RegistrationStatus.Approved);
    }

    // Missing (blank) and malformed fixture statuses fail the lookup, as a bad ReEx one does.
    [Theory]
    [InlineData("suspended")]
    [InlineData("")]
    public async Task GetAccreditationAsync_UnrecognisedFixtureRegistrationStatus_FailsWithOneErrorLog(
        string rawStatus
    )
    {
        var fakeOrgs = new FakeOrganisationPersistence();
        var registrationId = ObjectId.GenerateNewId();
        await fakeOrgs.CreateAsync(
            new OrganisationModel
            {
                OrgId = 70806,
                CompanyDetails = new CompanyDetailsModel { Name = "Unknown Status Ltd" },
                Registrations =
                [
                    new RegistrationModel
                    {
                        Id = registrationId,
                        Status = rawStatus,
                        Material = "plastic",
                        WasteProcessingType = "exporter",
                    },
                ],
            }
        );
        var logger = new CapturingLogger<StubReExApiAdapter>();

        var result = await StubLookupAsync("70806", registrationId.ToString(), fakeOrgs, logger);

        result.IsSuccess.Should().BeFalse();
        result.IsClientError.Should().BeTrue();
        result.StatusCode.Should().Be(500);
        var entry = logger.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error).Which;
        entry.Message.Should().Contain("70806").And.Contain(registrationId.ToString());
    }

    // A fixture organisation's registration must be in its fixture: a mistyped id fails as ReEx's 404 does, with an
    // Error log, rather than seeding a made-up approved registration.
    [Fact]
    public async Task GetAccreditationAsync_FixtureOrganisationWithoutThatRegistration_FailsNotFoundWithOneErrorLog()
    {
        var logger = new CapturingLogger<StubReExApiAdapter>();
        var registrationId = ObjectId.GenerateNewId().ToString();

        var result = await StubLookupAsync("50023", registrationId, logger: logger);

        result.IsSuccess.Should().BeFalse();
        result.IsNotFound.Should().BeTrue();
        result.StatusCode.Should().Be(404);
        var entry = logger.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error).Which;
        entry.Message.Should().Contain("50023").And.Contain(registrationId);
    }
}