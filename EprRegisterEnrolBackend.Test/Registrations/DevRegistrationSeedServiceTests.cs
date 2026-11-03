using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.Registrations;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.Registrations;

// RA-425: the local stack's Delta Green registration is Approved, so its material dashboard shows.
public sealed class DevRegistrationSeedServiceTests : IDisposable
{
    private static readonly string DeltaGreenRegistration =
        FakeOrganisationPersistence.Reg50003.ToString();

    private readonly TestMongoDbClientFactory _mongo;
    private readonly string _databaseName;
    private readonly RegistrationVersionStore _store;

    public DevRegistrationSeedServiceTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("dev_registration_seed");
        _mongo = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _store = new RegistrationVersionStore(
            new ServiceCollection().BuildServiceProvider(),
            _mongo,
            new TestClock(new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc))
        );
    }

    public void Dispose() => _mongo.GetClient().DropDatabase(_databaseName);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DevRegistrationSeedService CreateSut() =>
        new(_store, NullLogger<DevRegistrationSeedService>.Instance);

    private Task<List<RegistrationEntity>> DeltaGreenVersions() =>
        _store
            .Versions.FindAllCurrentAsync(
                Builders<RegistrationEntity>.Filter.Eq(
                    x => x.ReExRegistrationId,
                    DeltaGreenRegistration
                ),
                cancellationToken: Ct
            );

    [Fact]
    public async Task SeedAsync_WritesAnApprovedCurrentVersionForDeltaGreensRegistration()
    {
        await CreateSut().SeedAsync(Ct);

        var seeded = (await DeltaGreenVersions()).Should().ContainSingle().Subject;
        seeded.Status.Should().Be(RegistrationStatus.Approved);
        seeded.Version.Should().Be(1);
        seeded.Material.Should().Be(MaterialType.Plastic);
        seeded.WasteProcessingType.Should().Be(WasteProcessingKind.Reprocessor);
        seeded.RegistrationNumber.Should()
            .Be(DevAccreditationHistorySeedService.DeltaGreen2026RegistrationReference);
    }

    [Fact]
    public async Task SeedAsync_WritesAnApprovedCurrentVersionForTheManageExportersRegistration()
    {
        await CreateSut().SeedAsync(Ct);

        var seeded = (
            await _store.Versions.FindAllCurrentAsync(
                Builders<RegistrationEntity>.Filter.Eq(
                    x => x.ReExRegistrationId,
                    FakeOrganisationPersistence.Reg50018.ToString()
                ),
                cancellationToken: Ct
            )
        )
            .Should()
            .ContainSingle()
            .Subject;
        seeded.Status.Should().Be(RegistrationStatus.Approved);
        seeded.Material.Should().Be(MaterialType.Plastic);
        seeded.WasteProcessingType.Should().Be(WasteProcessingKind.Exporter);
        seeded.RegistrationNumber.Should().Be("R26EX0500180001PL");
    }

    [Fact]
    public async Task SeedAsync_RunTwice_ChangesNothing()
    {
        var sut = CreateSut();
        await sut.SeedAsync(Ct);
        var first = (await DeltaGreenVersions()).Single();

        await sut.SeedAsync(Ct);

        var versions = await _store.Versions.GetVersionsAsync(first.EntityId, cancellationToken: Ct);
        versions.Should().ContainSingle();
        versions[0].Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task SeedAsync_RegistrationAlreadyHasACurrentVersion_LeavesItAlone()
    {
        var existing = await _store.Versions.InsertAsync(
            new RegistrationEntity
            {
                ReExRegistrationId = DeltaGreenRegistration,
                Status = RegistrationStatus.Cancelled,
                Material = MaterialType.Plastic,
            },
            new ChangeInfo { Reason = ChangeReason.ReExImport },
            cancellationToken: Ct
        );

        await CreateSut().SeedAsync(Ct);

        var only = (await DeltaGreenVersions()).Should().ContainSingle().Subject;
        only.Id.Should().Be(existing.Id);
        only.Status.Should().Be(RegistrationStatus.Cancelled);
    }

    [Fact]
    public async Task SeedAsync_Cancelled_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => CreateSut().SeedAsync(cts.Token);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_SeedsInTheBackground()
    {
        var sut = CreateSut();

        await sut.StartAsync(Ct);
        await sut.ExecuteTask!;
        await sut.StopAsync(Ct);

        (await DeltaGreenVersions()).Should().ContainSingle();
    }
}
