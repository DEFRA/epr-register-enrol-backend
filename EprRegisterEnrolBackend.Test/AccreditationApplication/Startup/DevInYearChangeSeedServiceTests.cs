using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

public class DevInYearChangeSeedServiceTests
{
    private static DevInYearChangeSeedService CreateSut(IAccreditationApplicationPersistence persistence) =>
        new(persistence, NullLogger<DevInYearChangeSeedService>.Instance);

    [Fact]
    public async Task SeedAsync_InsertsOrg50018sApprovedExporterAccreditationOnce()
    {
        var persistence = new FakeAccreditationApplicationPersistence();
        var sut = CreateSut(persistence);

        await sut.SeedAsync(CancellationToken.None);
        await sut.SeedAsync(CancellationToken.None);

        var seeded = (await persistence.GetByOrganisationAsync("50018")).Should().ContainSingle().Subject;
        seeded.ApplicationStatus.Should().Be(ApplicationStatus.Approved);
        seeded.IsExporter.Should().BeTrue();
        seeded.RegistrationId.Should().Be(FakeOrganisationPersistence.Reg50018.ToString());
        seeded.OverseasSites!.Sites.Select(s => (s.OrsId, s.Selected))
            .Should()
            .Equal(("001", true), ("002", false), ("003", false), ("004", false));
    }

    [Fact]
    public async Task SeedAsync_PersistenceThrows_DoesNotThrow()
    {
        var persistence = Substitute.For<IAccreditationApplicationPersistence>();
        persistence.GetByIdAsync(default!, default!).ThrowsAsyncForAnyArgs(new InvalidOperationException("boom"));

        var act = () => CreateSut(persistence).SeedAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task FakeOrganisation50018_IsAnExporterWithTheSeededSites()
    {
        var org = await new FakeOrganisationPersistence().GetByOrgIdAsync(50018);

        org!.Registrations!.Single().OverseasSites.Should().Equal("900181", "900182", "900183", "900184");
    }
}
