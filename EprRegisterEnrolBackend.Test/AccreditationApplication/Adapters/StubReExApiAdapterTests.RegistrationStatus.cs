using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Organisation.Models;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

// RA-806: the stub carries its fixture registration's ReEx status, as HttpReExApiAdapter carries ReEx's.
public partial class StubReExApiAdapterTests
{
    private static async Task<ReExAccreditationDto> StubAccreditationAsync(
        string organisationId,
        string registrationId,
        FakeOrganisationPersistence? fakeOrgs = null,
        CapturingLogger<StubReExApiAdapter>? logger = null
    )
    {
        var sut = new StubReExApiAdapter(
            fakeOrgs ?? new FakeOrganisationPersistence(),
            logger ?? (ILogger<StubReExApiAdapter>)EnabledNullLogger<StubReExApiAdapter>.Instance
        );
        var result = await sut.GetAccreditationAsync(
            organisationId,
            registrationId,
            MaterialType.Plastic,
            2026
        );
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

    [Theory]
    [InlineData("suspended")]
    [InlineData("")]
    public async Task GetAccreditationAsync_UnrecognisedFixtureRegistrationStatus_IsNullAndLoggedAtError(
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

        var dto = await StubAccreditationAsync("70806", registrationId.ToString(), fakeOrgs, logger);

        dto.RegistrationStatus.Should().BeNull();
        var entry = logger.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error).Which;
        entry.Message.Should().Contain("registration status is missing or unrecognised");
        entry.Message.Should().Contain("70806");
        entry.Message.Should().Contain(registrationId.ToString());
    }
}
