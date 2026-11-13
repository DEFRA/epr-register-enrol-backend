using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
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

public class DevAccreditationHistorySeedServiceTests
{
    private const string DeltaGreen = "50003";

    private static DevAccreditationHistorySeedService CreateSut(
        IAccreditationApplicationPersistence persistence
    ) => new(persistence, NullLogger<DevAccreditationHistorySeedService>.Instance);

    private static async Task<List<AccreditationApplicationModel>> DeltaGreenApplications(
        FakeAccreditationApplicationPersistence persistence
    ) => (await persistence.GetByOrganisationAsync(DeltaGreen)).ToList();

    [Fact]
    public async Task StartAsync_SeedsInTheBackground()
    {
        var persistence = new FakeAccreditationApplicationPersistence();
        var sut = CreateSut(persistence);

        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!;
        await sut.StopAsync(CancellationToken.None);

        (await DeltaGreenApplications(persistence)).Should().ContainSingle();
    }

    [Fact]
    public async Task SeedAsync_CreateReturnsNull_DoesNotThrow()
    {
        var persistence = Substitute.For<IAccreditationApplicationPersistence>();
        persistence
            .GetByIdAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns((AccreditationApplicationModel?)null);
        persistence
            .CreateAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns((AccreditationApplicationModel?)null);

        var act = () => CreateSut(persistence).SeedAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        await persistence.Received(1).CreateAsync(Arg.Any<AccreditationApplicationModel>());
    }

    [Fact]
    public async Task SeedAsync_Cancelled_DoesNotSeedOrThrow()
    {
        var persistence = Substitute.For<IAccreditationApplicationPersistence>();
        persistence
            .GetByIdAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new TaskCompletionSource<AccreditationApplicationModel?>().Task);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => CreateSut(persistence).SeedAsync(cts.Token);

        await act.Should().NotThrowAsync();
        await persistence.DidNotReceive().CreateAsync(Arg.Any<AccreditationApplicationModel>());
    }

    [Fact]
    public async Task SeedAsync_AddsAnApprovedDeltaGreen2026PlasticAccreditation()
    {
        var persistence = new FakeAccreditationApplicationPersistence();

        await CreateSut(persistence).SeedAsync(CancellationToken.None);

        var seeded = (await DeltaGreenApplications(persistence)).Should().ContainSingle().Subject;
        seeded.Year.Should().Be(2026);
        seeded.RegistrationId.Should().Be(FakeOrganisationPersistence.Reg50003.ToString());
        seeded.MaterialType.Should().Be(MaterialType.Plastic);
        seeded.GlassRecyclingProcess.Should().BeNull();
        seeded.ApplicationStatus.Should().Be(ApplicationStatus.Approved);
        seeded.RegistrationReference.Should().Be("R25ER0500036908PL");
        seeded.AccreditationReference.Should().Be("A26ER0500030042PL");
    }

    [Fact]
    public async Task SeedAsync_RunTwice_SeedsOnlyOnce()
    {
        var persistence = new FakeAccreditationApplicationPersistence();
        var sut = CreateSut(persistence);

        await sut.SeedAsync(CancellationToken.None);
        await sut.SeedAsync(CancellationToken.None);

        (await DeltaGreenApplications(persistence)).Should().ContainSingle();
    }

    [Fact]
    public async Task SeedAsync_ExistingRecordChangedByATester_IsLeftAlone()
    {
        var persistence = new FakeAccreditationApplicationPersistence();
        var edited = DevAccreditationHistorySeedService.DeltaGreen2026Accreditation();
        edited.AccreditationReference = "A26ER0500031234PL";
        persistence.Seed(edited);

        await CreateSut(persistence).SeedAsync(CancellationToken.None);

        var only = (await DeltaGreenApplications(persistence)).Should().ContainSingle().Subject;
        only.AccreditationReference.Should().Be("A26ER0500031234PL");
    }

    [Fact]
    public async Task SeedAsync_PersistenceThrows_LogsAndDoesNotThrow()
    {
        var persistence = Substitute.For<IAccreditationApplicationPersistence>();
        persistence
            .GetByIdAsync(Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("mongo down"));

        var act = () => CreateSut(persistence).SeedAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // Guards drift: the seeded registration reference is what the stub would have given a 2026
    // application for this registration (Seed asks ReEx for the prior year).
    [Fact]
    public async Task SeededRegistrationReference_MatchesWhatTheStubGivesA2026Application()
    {
        var stub = new StubReExApiAdapter(
            new FakeOrganisationPersistence(),
            NullLogger<StubReExApiAdapter>.Instance
        );

        var result = await stub.GetAccreditationAsync(
            DeltaGreen,
            FakeOrganisationPersistence.Reg50003.ToString(),
            MaterialType.Plastic,
            2025
        );

        result.Value!.RegistrationReference.Should()
            .Be(DevAccreditationHistorySeedService.DeltaGreen2026RegistrationReference);
    }

    // The point of the seed: a 2027 Delta Green approval finds it as the previous accreditation,
    // and the two possible sources derive different references.
    [Fact]
    public async Task Seeded2026Accreditation_IsThePreviousAccreditationForA2027Application()
    {
        var persistence = new FakeAccreditationApplicationPersistence();
        await CreateSut(persistence).SeedAsync(CancellationToken.None);

        var previous = await persistence.GetLatestPreviousAccreditationAsync(
            DeltaGreen,
            FakeOrganisationPersistence.Reg50003.ToString(),
            MaterialType.Plastic,
            null,
            2027
        );

        previous.Should().NotBeNull();
        AccreditationReferenceDerivation
            .Derive(
                previous!.AccreditationReference,
                AccreditationReferenceSourceKind.PreviousAccreditation,
                2027
            )
            .Reference.Should()
            .Be("A27ER0500030042PL");
        AccreditationReferenceDerivation
            .Derive("R26ER0500036908PL", AccreditationReferenceSourceKind.Registration, 2027)
            .Reference.Should()
            .Be("A27ER0500036908PL", because: "the registration source must give a different answer");
    }
}
