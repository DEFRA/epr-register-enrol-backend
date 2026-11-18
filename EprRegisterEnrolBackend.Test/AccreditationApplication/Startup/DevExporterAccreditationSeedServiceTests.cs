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
        DevExporterOrg.Manage.RegistrationId.ToString();

    private static readonly TestClock Clock2026 = new(
        new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc)
    );

    private readonly TestMongoDbClientFactory _mongo;
    private readonly string _databaseName;
    private readonly RegistrationVersionStore _registrations;
    private readonly OverseasSiteVersionStore _overseasSites;

    public DevExporterAccreditationSeedServiceTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("dev_exporter_seed");
        _mongo = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _registrations = new RegistrationVersionStore(() => null, _mongo, Clock2026);
        _overseasSites = new OverseasSiteVersionStore(() => null, _mongo, Clock2026);
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
            _overseasSites,
            clock ?? Clock2026,
            NullLogger<DevExporterAccreditationSeedService>.Instance
        );

    private static async Task<List<AccreditationApplicationModel>> ExporterApplications(
        FakeAccreditationApplicationPersistence persistence,
        string organisationId = ExporterOrg
    ) => (await persistence.GetByOrganisationAsync(organisationId)).ToList();

    private Task<RegistrationEntity?> ExporterRegistrationVersion() =>
        _registrations.FindCurrentByReExRegistrationIdAsync(ExporterRegistration, Ct);

    private async Task<List<AccreditationApplicationModel>> SeedApplications(
        TimeProvider? clock = null,
        string organisationId = ExporterOrg
    )
    {
        var persistence = new FakeAccreditationApplicationPersistence();
        await CreateSut(persistence, clock).SeedAsync(Ct);
        return await ExporterApplications(persistence, organisationId);
    }

    private async Task<AccreditationApplicationModel> SeedOnce(
        TimeProvider? clock = null,
        string organisationId = ExporterOrg
    ) =>
        (await SeedApplications(clock, organisationId))
            .Should()
            .ContainSingle(a => a.IsExporter)
            .Subject;

    private async Task<AccreditationApplicationModel> SeedReprocessor(TimeProvider? clock = null) =>
        (await SeedApplications(clock)).Should().ContainSingle(a => !a.IsExporter).Subject;

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

    // RA-697: org 50018's glass reprocessor, the frontend stub's, so its manage dashboard and tonnage
    // change requests load against the real backend.
    [Fact]
    public async Task SeedAsync_AddsTheManageOrgsApprovedGlassReprocessorAccreditation()
    {
        var seeded = await SeedReprocessor();

        seeded.Id.Should().Be(ObjectId.Parse("b20260000000000000050018"));
        seeded.Year.Should().Be(2026);
        seeded.ApplicationStatus.Should().Be(ApplicationStatus.Approved);
        seeded.RegistrationId.Should().Be("bbb000000000000000050018");
        seeded.MaterialType.Should().Be(MaterialType.Glass);
        seeded.GlassRecyclingProcess.Should().Be(GlassRecyclingProcess.Remelt);
        seeded.IsExporter.Should().BeFalse();
        seeded.WasteProcessingType.Should().Be("reprocessor");
        seeded.OrganisationName.Should().Be("Manage Accreditation Test Ltd");
        seeded.SiteAddress.Should().Be("Unit 18, Furnace Lane, Doncaster, DN4 5HT");
        seeded.AccreditationReference.Should().Be("A26ER0500180002GR");
        seeded.RegistrationReference.Should().Be("R26ER0500180002GR");
        seeded.OverseasSites.Should().BeNull();

        var registration = await _registrations.FindCurrentByReExRegistrationIdAsync(
            "bbb000000000000000050018",
            Ct
        );
        registration!.Status.Should().Be(RegistrationStatus.Approved);
        registration.WasteProcessingType.Should().Be(WasteProcessingKind.Reprocessor);
        registration.Material.Should().Be(MaterialType.Glass);
        registration.RegistrationNumber.Should().Be("R26ER0500180002GR");
        registration.EntityId.Should().NotBe((await ExporterRegistrationVersion())!.EntityId);
    }

    [Fact]
    public async Task SeedAsync_InALaterYear_SeedsTheReprocessorForThatYear()
    {
        var seeded = await SeedReprocessor(
            new TestClock(new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        );

        seeded.Id.Should().Be(ObjectId.Parse("b20310000000000000050018"));
        seeded.AccreditationReference.Should().Be("A31ER0500180002GR");
    }

    // RA-697: org 50020, an Approved exporter with no change requests, for the empty states.
    [Fact]
    public async Task SeedAsync_SeedsTheNoChangeRequestsExporterForTheClocksYear()
    {
        var seeded = await SeedOnce(organisationId: "50020");

        seeded.Id.Should().Be(ObjectId.Parse("a20260000000000000050020"));
        seeded.ApplicationStatus.Should().Be(ApplicationStatus.Approved);
        seeded.RegistrationId.Should().Be("aaa000000000000000050020");
        seeded.MaterialType.Should().Be(MaterialType.Plastic);
        seeded.OrganisationName.Should().Be("No Change Requests Ltd");
        seeded.AccreditationReference.Should().Be("A26EX0500200001PL");
        seeded.OverseasSites!.Sites.Select(s => (s.OrsId, s.Selected))
            .Should()
            .Equal(("001", true), ("002", false));
        var registration = await _registrations.FindCurrentByReExRegistrationIdAsync(
            "aaa000000000000000050020",
            Ct
        );
        registration!.Status.Should().Be(RegistrationStatus.Approved);
    }

    // RA-697: org 50021, an Approved exporter registration with no accreditation in any year, for the
    // registration-only manage pages.
    [Fact]
    public async Task SeedAsync_GivesTheRegistrationOnlyExporterAnApprovedRegistrationAndNoAccreditation()
    {
        (await SeedApplications(organisationId: "50021")).Should().BeEmpty();

        var registration = await _registrations.FindCurrentByReExRegistrationIdAsync(
            "aaa000000000000000050021",
            Ct
        );
        registration!.Status.Should().Be(RegistrationStatus.Approved);
        registration.WasteProcessingType.Should().Be(WasteProcessingKind.Exporter);
        registration.Material.Should().Be(MaterialType.Plastic);
        registration.RegistrationNumber.Should().Be("R26EX0500210001PL");
        registration.EntityId.Should().Be(ObjectId.Parse("0e0000000000000000050021"));
    }

    // RA-697 task 011: org 50021's registered ORSs, which its registration-only change requests view lists.
    [Fact]
    public async Task SeedAsync_GivesTheRegistrationOnlyExporterFourActiveOrsRecordsWithInterimSites()
    {
        await SeedApplications(organisationId: "50021");
        var registration = await _registrations.FindCurrentByReExRegistrationIdAsync(
            "aaa000000000000000050021",
            Ct
        );

        var sites = await _overseasSites.FindActiveByRegistrationAsync(registration!.EntityId, Ct);

        sites.OrderBy(s => s.OrsId)
            .Select(s => (s.OrsId, s.SiteName, s.Country, s.InterimSites.Count))
            .Should()
            .Equal(
                ("001", "Porto Plasticos Lda", "Portugal", 2),
                ("002", "Milano Riciclo SpA", "Italy", 1),
                ("003", "Penang Polymers Sdn Bhd", "Malaysia", 0),
                ("004", "Jakarta Daur Ulang PT", "Indonesia", 0)
            );
        sites.Should().OnlyContain(s => s.Status == OverseasSiteStatus.Active);
        sites.SelectMany(s => s.InterimSites).Should().OnlyContain(i => i.Status == InterimSiteStatus.Active);
        sites.SelectMany(s => s.InterimSites).Select(i => i.SiteNumber).Should().OnlyHaveUniqueItems();
        sites.Should().OnlyContain(s => s.Contact != null && s.Coordinates != null && s.OperationCodes.Count > 0);
    }

    [Fact]
    public async Task SeedAsync_RunTwice_SeedsTheRegistrationOnlyExportersOrsRecordsOnce()
    {
        await SeedApplications(organisationId: "50021");
        await SeedApplications(organisationId: "50021");
        var registration = await _registrations.FindCurrentByReExRegistrationIdAsync(
            "aaa000000000000000050021",
            Ct
        );

        (await _overseasSites.FindActiveByRegistrationAsync(registration!.EntityId, Ct))
            .Should()
            .HaveCount(4);
    }

    // RA-697: org 50022, registered with no accreditation and no ORSs, for the overseas-sites page's empty tabs.
    [Fact]
    public async Task SeedAsync_GivesTheNoOverseasSitesExporterAnApprovedRegistrationWithNoAccreditationOrOrsRecords()
    {
        (await SeedApplications(organisationId: "50022")).Should().BeEmpty();

        var registration = await _registrations.FindCurrentByReExRegistrationIdAsync(
            "aaa000000000000000050022",
            Ct
        );
        registration!.Status.Should().Be(RegistrationStatus.Approved);
        registration.WasteProcessingType.Should().Be(WasteProcessingKind.Exporter);
        (await _overseasSites.FindActiveByRegistrationAsync(registration.EntityId, Ct)).Should().BeEmpty();

        var org = await new FakeOrganisationPersistence(Clock2026, includeDevExporters: true).GetByOrgIdAsync(50022);
        org!.CompanyDetails!.Name.Should().Be("No Overseas Sites Exports Ltd");
        org.Registrations!.Should().ContainSingle().Which.Id.ToString().Should().Be(registration.ReExRegistrationId);
    }

    // The accredited orgs' ORSs come from their accreditations, as before; only 50021's are domain-model records.
    [Fact]
    public async Task SeedAsync_GivesTheAccreditedExportersNoOrsRecords()
    {
        await SeedApplications();
        var registration = await ExporterRegistrationVersion();

        (await _overseasSites.FindActiveByRegistrationAsync(registration!.EntityId, Ct))
            .Should()
            .BeEmpty();
    }

    // Guards drift: the stub ReEx registry's org 50021 is the registration the seed writes.
    [Fact]
    public async Task StubOrganisationStore_MatchesTheSeededRegistrationOnlyExporter()
    {
        await SeedApplications(organisationId: "50021");
        var seeded = await _registrations.FindCurrentByReExRegistrationIdAsync(
            "aaa000000000000000050021",
            Ct
        );
        var org = await new FakeOrganisationPersistence(
            Clock2026,
            includeDevExporters: true
        ).GetByOrgIdAsync(50021);

        org!.CompanyDetails!.Name.Should().Be("Registration Only Exports Ltd");
        org.CompanyDetails.TradingName.Should().Be("Registration Only Exports");
        org.CompanyDetails.RegistrationNumber.Should().Be(seeded!.RegistrationNumber);
        org.WasteProcessingTypes.Should().Equal("exporter");
        var registration = org.Registrations!.Should().ContainSingle().Subject;
        registration.Id.ToString().Should().Be(seeded.ReExRegistrationId);
        registration.Material.Should().Be("plastic");
        registration.WasteProcessingType.Should().Be("exporter");
    }

    // RA-492: org 50019, for the in-year "add a registered ORS to your accreditation" journey.
    [Fact]
    public async Task SeedAsync_SeedsTheInYearChangeExporterForTheClocksYear()
    {
        var seeded = await SeedOnce(organisationId: "50019");

        seeded.Id.Should().Be(ObjectId.Parse("a20260000000000000050019"));
        seeded.Year.Should().Be(2026);
        seeded.ApplicationStatus.Should().Be(ApplicationStatus.Approved);
        seeded.RegistrationId.Should().Be("aaa000000000000000050019");
        seeded.MaterialType.Should().Be(MaterialType.Plastic);
        seeded.IsExporter.Should().BeTrue();
        seeded.OrganisationName.Should().Be("In-Year Change Exports Ltd");
        seeded.AccreditationReference.Should().Be("A26EX0500190001PL");
        seeded.RegistrationReference.Should().Be("R26EX0500190001PL");
        var registration = await _registrations.FindCurrentByReExRegistrationIdAsync(
            "aaa000000000000000050019",
            Ct
        );
        registration!.Status.Should().Be(RegistrationStatus.Approved);
        registration.RegistrationNumber.Should().Be("R26EX0500190001PL");
    }

    [Fact]
    public async Task SeedAsync_InYearChangeExporterHasOneAccreditedFourRegisteredOnlyAndOneNeverRegisteredOrs()
    {
        var seeded = await SeedOnce(organisationId: "50019");
        var sites = seeded.OverseasSites!.Sites;

        // The Registered tab lists them in this order: the two complete ones first.
        sites.Select(s => (s.SiteId, s.OrsId, s.SiteName, s.Country, s.Selected))
            .Should()
            .Equal(
                (901901, "001", "Hamburg Kunststoff GmbH", "Germany", true),
                (901910, "005", "Lyon Plastiques SA", "France", false),
                (901914, "009", "Rotterdam Recycling BV", "Netherlands", false),
                (901911, "006", "Delhi Polymers Pvt", "India", false),
                (901912, "007", "Izmir Recycling AS", "Turkey", false),
                (901913, "008", "Valencia Reciclaje SL", "Spain", false)
            );
        sites.Select(s => (s.Coordinates, string.Join(",", s.OperationCodes), s.ConditionsOfExport, s.IsEu, s.IsOecd))
            .Should()
            .Equal(
                ("53.5511, 9.9937", "R3", true, true, true),
                ("45.7640, 4.8357", "R3", true, true, true),
                ("51.9244, 4.4777", "R3", true, true, true),
                (null, "", null, false, false),
                ("38.4237, 27.1428", "", null, false, true),
                ("39.4699, -0.3763", "R3", true, true, true)
            );
        // Added by the operator and deselected: never registered, so on neither tab.
        sites.Where(s => s.IsNewSite).Select(s => s.SiteId).Should().Equal(901913);
        sites[0].ValidFrom.Should().Be("2026-01-01T00:00:00Z");
        sites.Should().OnlyContain(s => s.ContactName != null && s.Code1 != null);
        // RA-739: Rotterdam has one interim site, so Case Management's Application summary shows the section.
        sites.Where(s => s.InterimSites.Count > 0).Select(s => s.SiteId).Should().Equal(901914);
        var rotterdam = sites.Single(s => s.SiteId == 901914);
        rotterdam.InterimSites.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { SiteId = 901915, SiteNumber = "001", SiteName = "Antwerp Transfer Hub", Country = "Belgium", RemovedAt = (DateTime?)null });
        rotterdam.InterimSite!.SiteId.Should().Be(901915);
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

        // The exporter and the reprocessor, once each.
        (await ExporterApplications(persistence)).Should().HaveCount(2);
        foreach (var reExRegistrationId in new[] { ExporterRegistration, "bbb000000000000000050018" })
        {
            var registration = await _registrations.FindCurrentByReExRegistrationIdAsync(reExRegistrationId, Ct);
            (await _registrations.Versions.GetVersionsAsync(registration!.EntityId, cancellationToken: Ct))
                .Should()
                .ContainSingle();
        }
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
        var edited = DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.Manage, 2026);
        edited.AccreditationReference = "A26EX0500189999PL";
        persistence.Seed(edited);

        await CreateSut(persistence).SeedAsync(CancellationToken.None);

        var only = (await ExporterApplications(persistence)).Should().ContainSingle(a => a.IsExporter).Subject;
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
        await persistence.Received(1).CreateAsync(Arg.Is<AccreditationApplicationModel>(a => a.OrgId == 50018 && a.IsExporter));
        await persistence.Received(1).CreateAsync(Arg.Is<AccreditationApplicationModel>(a => a.OrgId == 50018 && !a.IsExporter));
        await persistence.Received(1).CreateAsync(Arg.Is<AccreditationApplicationModel>(a => a.OrgId == 50019));
        await persistence.Received(1).CreateAsync(Arg.Is<AccreditationApplicationModel>(a => a.OrgId == 50020));
        await persistence.DidNotReceive().CreateAsync(Arg.Is<AccreditationApplicationModel>(a => a.OrgId == 50021));
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

        (await ExporterApplications(persistence)).Should().HaveCount(2);
        (await ExporterRegistrationVersion()).Should().NotBeNull();
    }

    // Guards drift: the stub ReEx registry's orgs 50018, 50019 and 50020 are the organisations,
    // registrations and ORSs the seed writes.
    [Theory]
    [InlineData(50018)]
    [InlineData(50019)]
    [InlineData(50020)]
    public async Task StubOrganisationStore_MatchesTheSeededExporter(int orgId)
    {
        var seeded = await SeedOnce(organisationId: orgId.ToString());
        var org = await new FakeOrganisationPersistence(
            Clock2026,
            includeDevExporters: true
        ).GetByOrgIdAsync(orgId);

        org!.CompanyDetails!.Name.Should().Be(seeded.OrganisationName);
        org.CompanyDetails.RegistrationNumber.Should().Be(seeded.RegistrationReference);
        org.CompanyDetails.CompaniesHouseNumber.Should().Be(seeded.CompaniesHouseNumber);
        org.WasteProcessingTypes.Should().Contain("exporter");
        var registration = org.Registrations!.Single(r => r.WasteProcessingType == "exporter");
        registration.Id.ToString().Should().Be(seeded.RegistrationId);
        registration.Material.Should().Be("plastic");
        registration.WasteProcessingType.Should().Be("exporter");
        // The registration lists every seeded ORS but a never-registered one (org 50019's 901913).
        registration.OverseasSites.Should()
            .Equal(seeded.OverseasSites!.Sites.Where(s => !s.IsNewSite).Select(s => s.SiteId.ToString()));
        registration.WasteManagementPermits!.Select(p => p.PermitNumber)
            .Should()
            .Equal(seeded.PermitNumbers);
    }

    // Guards drift: org 50018's reprocessor registration in the stub ReEx registry is the one seeded.
    [Fact]
    public async Task StubOrganisationStore_MatchesTheSeededReprocessor()
    {
        var seeded = await SeedReprocessor();
        var org = await new FakeOrganisationPersistence(
            Clock2026,
            includeDevExporters: true
        ).GetByOrgIdAsync(50018);

        org!.WasteProcessingTypes.Should().Equal("exporter", "reprocessor");
        var registration = org.Registrations!.Single(r => r.WasteProcessingType == "reprocessor");
        registration.Id.ToString().Should().Be(seeded.RegistrationId);
        registration.Material.Should().Be("glass");
        registration.OverseasSites.Should().BeNullOrEmpty();
        registration.WasteManagementPermits!.Select(p => p.PermitNumber)
            .Should()
            .Equal(seeded.PermitNumbers);
    }
}
