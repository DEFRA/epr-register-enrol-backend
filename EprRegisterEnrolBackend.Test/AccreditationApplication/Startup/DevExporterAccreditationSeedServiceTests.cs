using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DevSeed;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.Registrations;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using MongoDB.Bson;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

// RA-425: the local stack's dedicated exporter (stub org 50018) has an Approved current-year
// accreditation with ORSs and interim sites, and an Approved registration version, so its material
// dashboard pages show content.
public sealed class DevExporterAccreditationSeedServiceTests : IDisposable
{
    private const string ExporterOrg = "50018";
    private static readonly string ExporterRegistration =
        ManageExporterOrg.RegistrationId.ToString();

    private static readonly TestClock Clock2026 = new(
        new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc)
    );

    private readonly TestMongoDbClientFactory _mongo;
    private readonly string _databaseName;
    private readonly RegistrationVersionStore _registrations;

    public DevExporterAccreditationSeedServiceTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("dev_exporter_seed");
        _mongo = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _registrations = new RegistrationVersionStore(() => null, _mongo, Clock2026);
    }

    public void Dispose() => _mongo.GetClient().DropDatabase(_databaseName);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DevExporterAccreditationSeedService CreateSut(
        IAccreditationApplicationPersistence persistence,
        TimeProvider? clock = null
    ) =>
        new(
            persistence,
            _registrations,
            clock ?? Clock2026,
            NullLogger<DevExporterAccreditationSeedService>.Instance
        );

    private static async Task<List<AccreditationApplicationModel>> ExporterApplications(
        FakeAccreditationApplicationPersistence persistence
    ) => (await persistence.GetByOrganisationAsync(ExporterOrg)).ToList();

    private Task<RegistrationEntity?> ExporterRegistrationVersion() =>
        _registrations.FindCurrentByReExRegistrationIdAsync(ExporterRegistration, Ct);

    private async Task<AccreditationApplicationModel> SeedOnce(TimeProvider? clock = null)
    {
        var persistence = new FakeAccreditationApplicationPersistence();
        await CreateSut(persistence, clock).SeedAsync(Ct);
        return (await ExporterApplications(persistence)).Should().ContainSingle().Subject;
    }

    private static OverseasSiteModel Ors(AccreditationApplicationModel application, string orsId) =>
        application.OverseasSites!.Sites.Single(s => s.OrsId == orsId);

    [Fact]
    public async Task SeedAsync_AddsAnApprovedPlasticExporterAccreditationForTheClocksYear()
    {
        var seeded = await SeedOnce();

        seeded.Id.Should().Be(ObjectId.Parse("a20260000000000000050018"));
        seeded.Year.Should().Be(2026);
        seeded.ApplicationStatus.Should().Be(ApplicationStatus.Approved);
        seeded.RegistrationId.Should().Be("aaa000000000000000050018");
        seeded.MaterialType.Should().Be(MaterialType.Plastic);
        seeded.IsExporter.Should().BeTrue();
        seeded.WasteProcessingType.Should().Be("exporter");
        seeded.OrganisationName.Should().Be("Manage Accreditation Test Ltd");
        seeded.AccreditationReference.Should().Be("A26EX0500180001PL");
        seeded.RegistrationReference.Should().Be("R26EX0500180001PL");
    }

    [Fact]
    public async Task SeedAsync_InALaterYear_SeedsThatYear()
    {
        var seeded = await SeedOnce(
            new TestClock(new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        );

        seeded.Year.Should().Be(2031);
        seeded.AccreditationReference.Should().Be("A31EX0500180001PL");
        seeded.RegistrationReference.Should().Be("R31EX0500180001PL");
    }

    [Fact]
    public async Task SeedAsync_SeedsThreeAccreditedSitesAndOneRegisteredOnly()
    {
        var seeded = await SeedOnce();

        var sites = seeded.OverseasSites!.Sites;
        sites.Where(s => s.Selected).Select(s => s.OrsId).Should().Equal("001", "002", "003");
        sites.Where(s => !s.Selected).Select(s => s.OrsId).Should().Equal("004");
        sites.Should().OnlyContain(s => !s.IsNewSite);
        sites.Should().OnlyContain(s => s.Country != null && s.AddressLine1 != null);
    }

    [Fact]
    public async Task SeedAsync_InterimSitesIncludeARemovedOneAndOneSiteServingTwoOrss()
    {
        var seeded = await SeedOnce();

        var ors001 = Ors(seeded, "001").InterimSites;
        ors001.Select(i => i.SiteName)
            .Should()
            .Equal("Calais Storage SARL", "Antwerp Transfer Hub", "Old Depot GmbH");
        ors001.Single(i => i.SiteName == "Old Depot GmbH").RemovedAt.Should().NotBeNull();
        ors001.Where(i => i.SiteName != "Old Depot GmbH")
            .Should()
            .OnlyContain(i => i.RemovedAt == null);

        var antwerp = ors001.Single(i => i.SiteName == "Antwerp Transfer Hub");
        var ors002 = Ors(seeded, "002").InterimSites.Should().ContainSingle().Subject;
        ors002.SiteName.Should().Be(antwerp.SiteName);
        ors002.RemovedAt.Should().BeNull();

        Ors(seeded, "003").InterimSites.Should().BeEmpty();
    }

    // The overseas-sites page must hide a registered-only ORS's interim sites, so the seed gives it one.
    [Fact]
    public async Task SeedAsync_RegisteredOnlyOrsHasAnInterimSite()
    {
        var seeded = await SeedOnce();

        var ors004 = Ors(seeded, "004");
        ors004.Selected.Should().BeFalse();
        ors004.InterimSites.Should().ContainSingle().Which.RemovedAt.Should().BeNull();
    }

    // Interim site ids key the edit routes and test ids, and numbers are unique within an application.
    [Fact]
    public async Task SeedAsync_InterimSiteIdsAndNumbersAreUnique()
    {
        var seeded = await SeedOnce();

        var interimSites = seeded.OverseasSites!.Sites.SelectMany(s => s.InterimSites).ToList();
        interimSites.Select(i => i.SiteId).Should().OnlyHaveUniqueItems();
        interimSites.Select(i => i.SiteNumber).Should().OnlyHaveUniqueItems();
    }

    // The legacy single-interim-site mirror must never point at a withdrawn site.
    [Fact]
    public async Task SeedAsync_InterimSiteMirrorIsTheFirstActiveSite()
    {
        var seeded = await SeedOnce();

        Ors(seeded, "001").InterimSite!.SiteName.Should().Be("Calais Storage SARL");
        Ors(seeded, "003").InterimSite.Should().BeNull();
    }

    [Fact]
    public async Task SeedAsync_RunTwice_SeedsOnlyOnce()
    {
        var persistence = new FakeAccreditationApplicationPersistence();
        var sut = CreateSut(persistence);

        await sut.SeedAsync(CancellationToken.None);
        await sut.SeedAsync(CancellationToken.None);

        (await ExporterApplications(persistence)).Should().ContainSingle();
        var registration = await ExporterRegistrationVersion();
        (await _registrations.Versions.GetVersionsAsync(registration!.EntityId, cancellationToken: Ct))
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task SeedAsync_WritesAnApprovedCurrentVersionForTheExportersRegistration()
    {
        await SeedOnce();

        var seeded = (await ExporterRegistrationVersion())!;
        seeded.Status.Should().Be(RegistrationStatus.Approved);
        seeded.Version.Should().Be(1);
        seeded.Material.Should().Be(MaterialType.Plastic);
        seeded.WasteProcessingType.Should().Be(WasteProcessingKind.Exporter);
        seeded.RegistrationNumber.Should().Be("R26EX0500180001PL");
        seeded.EntityId.Should().Be(ObjectId.Parse("0e0000000000000000050018"));
        seeded.OrganisationId.Should().Be(ObjectId.Parse("0d0000000000000000050018"));
    }

    [Fact]
    public async Task SeedAsync_InALaterYear_NumbersTheRegistrationForThatYear()
    {
        await SeedOnce(new TestClock(new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        (await ExporterRegistrationVersion())!.RegistrationNumber.Should().Be("R31EX0500180001PL");
    }

    // The fe-tests stack seeds only this organisation: the Delta Green data is DevSeedData:Enabled's.
    [Fact]
    public async Task SeedAsync_SeedsNoOtherRegistration()
    {
        await SeedOnce();

        (
            await _registrations.FindCurrentByReExRegistrationIdAsync(
                FakeOrganisationPersistence.Reg50003.ToString(),
                Ct
            )
        )
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task SeedAsync_RegistrationAlreadyHasACurrentVersion_LeavesItAlone()
    {
        var existing = await _registrations.Versions.InsertAsync(
            new RegistrationEntity
            {
                ReExRegistrationId = ExporterRegistration,
                Status = RegistrationStatus.Cancelled,
                Material = MaterialType.Plastic,
            },
            new ChangeInfo { Reason = ChangeReason.ReExImport },
            cancellationToken: Ct
        );

        await SeedOnce();

        var only = (await ExporterRegistrationVersion())!;
        only.Id.Should().Be(existing.Id);
        only.Status.Should().Be(RegistrationStatus.Cancelled);
    }

    [Fact]
    public async Task SeedAsync_ExistingRecordChangedByATester_IsLeftAlone()
    {
        var persistence = new FakeAccreditationApplicationPersistence();
        var edited = DevExporterAccreditationSeedService.ExporterAccreditation(2026);
        edited.AccreditationReference = "A26EX0500189999PL";
        persistence.Seed(edited);

        await CreateSut(persistence).SeedAsync(CancellationToken.None);

        var only = (await ExporterApplications(persistence)).Should().ContainSingle().Subject;
        only.AccreditationReference.Should().Be("A26EX0500189999PL");
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
        (await ExporterRegistrationVersion()).Should().NotBeNull();
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
    public async Task SeedAsync_PersistenceThrows_LogsAndDoesNotThrow()
    {
        var persistence = Substitute.For<IAccreditationApplicationPersistence>();
        persistence
            .GetByIdAsync(Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("mongo down"));

        var act = () => CreateSut(persistence).SeedAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_SeedsInTheBackground()
    {
        var persistence = new FakeAccreditationApplicationPersistence();
        var sut = CreateSut(persistence);

        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!;
        await sut.StopAsync(CancellationToken.None);

        (await ExporterApplications(persistence)).Should().ContainSingle();
        (await ExporterRegistrationVersion()).Should().NotBeNull();
    }

    // Guards drift: the stub ReEx registry's org 50018 is the organisation, registration and ORSs
    // the seed writes.
    [Fact]
    public async Task StubOrganisationStore_MatchesTheSeededExporter()
    {
        var seeded = await SeedOnce();
        var org = await new FakeOrganisationPersistence(
            Clock2026,
            includeManageExporter: true
        ).GetByOrgIdAsync(50018);

        org!.CompanyDetails!.Name.Should().Be(seeded.OrganisationName);
        org.CompanyDetails.RegistrationNumber.Should().Be(seeded.RegistrationReference);
        org.CompanyDetails.CompaniesHouseNumber.Should().Be(seeded.CompaniesHouseNumber);
        org.WasteProcessingTypes.Should().Equal("exporter");
        var registration = org.Registrations!.Should().ContainSingle().Subject;
        registration.Id.ToString().Should().Be(seeded.RegistrationId);
        registration.Material.Should().Be("plastic");
        registration.WasteProcessingType.Should().Be("exporter");
        registration.OverseasSites.Should()
            .Equal(seeded.OverseasSites!.Sites.Select(s => s.SiteId.ToString()));
        registration.WasteManagementPermits!.Select(p => p.PermitNumber)
            .Should()
            .Equal(seeded.PermitNumbers);
    }
}
