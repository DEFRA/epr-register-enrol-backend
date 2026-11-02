using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Test.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Migration;

/// <summary>
/// B8b: ensure-migrated over a registration group (design doc §6.1, §6.2), against a real single-node replica set
/// so the transaction and the unique indexes are real.
/// </summary>
public sealed class ApplicationGroupMigratorTests : IDisposable
{
    private const string ReExOrganisationId = "6512a0f0c0ffee0000000001";
    private const string ReExRegistrationId = "6512a0f0c0ffee0000000002";
    private const string RegistrationNumber = "R27EX1234567P";

    private static readonly DateTime Day1 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = Day1.AddDays(1);
    private static readonly DateTime Day3 = Day1.AddDays(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly AccreditationApplicationPersistence _applications;
    private readonly ApplicationGroupStores _stores;
    private readonly ApplicationGroupMigrator _migrator;

    public ApplicationGroupMigratorTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("groupmigrate");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        var clock = new TestClock(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        var logs = NullLoggerFactory.Instance;
        _applications = new AccreditationApplicationPersistence(_factory, logs);
        _stores = new ApplicationGroupStores(
            new OrganisationPersistence(_factory, logs),
            new RegistrationPersistence(_factory, logs, clock),
            new OverseasSitePersistence(_factory, logs, clock),
            new ApplicationVersionPersistence(_factory, logs, clock),
            new AccreditationPersistence(_factory, logs, clock),
            new MigrationConflictPersistence(_factory, logs)
        );
        _migrator = new ApplicationGroupMigrator(_applications, _stores, clock);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    // --- fixtures -------------------------------------------------------------------------------------------------

    private static OverseasSiteModel Site(
        int siteId,
        string? orsId,
        string name,
        bool isNew = false,
        params InterimSiteModel[] interimSites
    ) =>
        new()
        {
            SiteId = siteId,
            OrsId = orsId,
            SiteName = name,
            Country = "France",
            Code1 = "B3011",
            IsNewSite = isNew,
            InterimSites = [.. interimSites],
        };

    private static InterimSiteModel Interim(int siteId, string siteNumber, bool isNew = true) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = siteNumber,
            Country = "Belgium",
            SiteName = "Interim",
            AddressLine1 = "1 Quai",
            TownOrCity = "Antwerp",
            ContactName = "Contact",
            ContactEmail = "contact@example.com",
            ContactPhone = "000",
            IsNewSite = isNew,
        };

    private static AccreditationApplicationModel NewApplication(
        ApplicationStatus status,
        DateTime updatedAt,
        params OverseasSiteModel[] sites
    ) =>
        new()
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = ReExOrganisationId,
            OrganisationName = "Acme Recycling Ltd",
            RegistrationId = ReExRegistrationId,
            RegistrationReference = RegistrationNumber,
            IsExporter = true,
            Nation = Nation.England,
            MaterialType = MaterialType.Plastic,
            Year = 2027,
            ApplicationStatus = status,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            DateLastEdited = updatedAt,
            OverseasSites = new AccreditationApplicationOverseasSites { Sites = [.. sites] },
        };

    private async Task<AccreditationApplicationModel> ApplicationAsync(
        ApplicationStatus status,
        DateTime updatedAt,
        params OverseasSiteModel[] sites
    ) => (await _applications.CreateAsync(NewApplication(status, updatedAt, sites)))!;

    private async Task<AccreditationApplicationModel> ApplicationForYearAsync(
        int year,
        ApplicationStatus status,
        DateTime updatedAt,
        params OverseasSiteModel[] sites
    )
    {
        var application = NewApplication(status, updatedAt, sites);
        application.Year = year;
        return (await _applications.CreateAsync(application))!;
    }

    private Task<GroupMigrationResult> MigrateAsync(AccreditationApplicationModel application) =>
        MigrateAsync(application.Id!.Value);

    private Task<GroupMigrationResult> MigrateAsync(ObjectId applicationId) =>
        InsertRaceRetry.RunInTransactionAsync(
            _factory,
            (session, token) => _migrator.EnsureMigratedAsync(applicationId, session, token),
            _ => { },
            Ct
        );

    private Task<List<T>> AllAsync<T>(string collection, FilterDefinition<T>? filter = null) =>
        _factory
            .GetCollection<T>(collection)
            .Find(filter ?? FilterDefinition<T>.Empty)
            .ToListAsync(Ct);

    private async Task<List<T>> CurrentAsync<T>(string collection)
        where T : VersionedEntityBase =>
        await AllAsync(collection, Builders<T>.Filter.Eq(x => x.IsCurrent, true));

    private async Task<OverseasSiteEntity> CurrentSiteAsync(string? orsId) =>
        Assert.Single(
            await CurrentAsync<OverseasSiteEntity>(DomainModelCollections.OverseasSiteVersions),
            s => s.OrsId == orsId
        );

    private async Task<ApplicationEntity> CurrentApplicationAsync(
        AccreditationApplicationModel application
    ) =>
        Assert.Single(
            await CurrentAsync<ApplicationEntity>(DomainModelCollections.ApplicationVersions),
            a => a.EntityId == application.Id
        );

    private Task<AccreditationApplicationModel?> LegacyAsync(
        AccreditationApplicationModel application
    ) => _applications.GetByIdAsync(ReExOrganisationId, application.Id.ToString()!);

    private Task<List<MigrationConflict>> ConflictsAsync() =>
        AllAsync<MigrationConflict>(DomainModelCollections.MigrationConflicts);

    // --- tests ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FirstTouch_MigratesTheWholeRegistrationGroup()
    {
        var saved = await ApplicationAsync(
            ApplicationStatus.Saved,
            Day2,
            Site(1, "001", "Site One"),
            Site(2, "002", "New Site", isNew: true, Interim(3, "001"))
        );
        var rejected = await ApplicationAsync(
            ApplicationStatus.Rejected,
            Day1,
            Site(1, "001", "Site One")
        );

        var result = await MigrateAsync(rejected);

        Assert.Equal(GroupMigrationOutcome.Migrated, result.Outcome);
        Assert.Equal(2, result.MigratedApplications);

        var organisation = Assert.Single(
            await AllAsync<OrganisationEntity>(DomainModelCollections.Organisations)
        );
        Assert.Equal(ReExOrganisationId, organisation.ReExOrganisationId);
        Assert.Equal("Acme Recycling Ltd", organisation.Name);

        var registration = Assert.Single(
            await AllAsync<RegistrationEntity>(DomainModelCollections.RegistrationVersions)
        );
        Assert.Equal(ReExRegistrationId, registration.ReExRegistrationId);
        Assert.Equal(organisation.Id, registration.OrganisationId);
        Assert.Equal(RegistrationNumber, registration.RegistrationNumber);
        Assert.Equal(WasteProcessingKind.Exporter, registration.WasteProcessingType);
        Assert.Equal(SourceSystem.Application, registration.Source.System);
        Assert.True(registration.Source.EnrichmentPending);
        Assert.Equal(ChangeReason.Migration, registration.LastChange.Reason);

        var reExSite = await CurrentSiteAsync("001");
        Assert.Equal(OverseasSiteStatus.Active, reExSite.Status);
        Assert.Equal(SiteOrigin.ReEx, reExSite.Origin);
        Assert.Equal(registration.EntityId, reExSite.RegistrationId);
        Assert.Equal(["B3011"], reExSite.BaselOecdCodes);

        var newSite = await CurrentSiteAsync("002");
        Assert.Equal(OverseasSiteStatus.Proposed, newSite.Status);
        Assert.Equal(SiteOrigin.Operator, newSite.Origin);
        Assert.Equal(saved.Id, newSite.ProposedByApplicationId);
        var interim = Assert.Single(newSite.InterimSites);
        Assert.Equal("001", interim.SiteNumber);
        Assert.Equal(InterimSiteStatus.Proposed, interim.Status);
        Assert.Null(interim.CreatedAt);

        var savedVersion = await CurrentApplicationAsync(saved);
        Assert.Equal(1, savedVersion.Version);
        Assert.Equal(registration.EntityId, savedVersion.RegistrationId);
        Assert.Equal(ApplicationStatus.Saved, savedVersion.Status);
        var savedNewSite = Assert.Single(savedVersion.ApplicationOverseasSites, e => e.SiteId == 2);
        Assert.Equal(newSite.EntityId, savedNewSite.OverseasSiteId);
        Assert.True(savedNewSite.IsNewSite);
        var interimEntry = Assert.Single(savedNewSite.InterimSites);
        Assert.Equal("001", interimEntry.SiteNumber);
        Assert.Equal(3, interimEntry.LegacySiteId);
        Assert.Equal(ApplicationStatus.Rejected, (await CurrentApplicationAsync(rejected)).Status);

        var legacy = (await LegacyAsync(saved))!;
        Assert.Equal(registration.EntityId, legacy.RegistrationEntityId);
        Assert.Equal(reExSite.EntityId, legacy.OverseasSites!.Sites[0].OverseasSiteEntityId);
        Assert.Equal(newSite.EntityId, legacy.OverseasSites.Sites[1].OverseasSiteEntityId);
        Assert.Equal(0, legacy.Version);
        Assert.Equal(registration.EntityId, (await LegacyAsync(rejected))!.RegistrationEntityId);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public async Task SecondTouch_DoesNothing()
    {
        var application = await ApplicationAsync(
            ApplicationStatus.Saved,
            Day1,
            Site(1, "001", "Site One")
        );
        await MigrateAsync(application);

        var again = await MigrateAsync(application);

        Assert.Equal(GroupMigrationOutcome.AlreadyMigrated, again.Outcome);
        Assert.Equal(0, again.MigratedApplications);
        Assert.Single(
            await AllAsync<RegistrationEntity>(DomainModelCollections.RegistrationVersions)
        );
        Assert.Single(
            await AllAsync<OverseasSiteEntity>(DomainModelCollections.OverseasSiteVersions)
        );
        Assert.Single(
            await AllAsync<ApplicationEntity>(DomainModelCollections.ApplicationVersions)
        );
    }

    [Fact]
    public async Task NewApplicationInAMigratedGroup_IsMigratedOntoTheExistingSites()
    {
        // A withdrawn application is not live, so the operator can start again for the same year.
        var first = await ApplicationAsync(
            ApplicationStatus.Withdrawn,
            Day1,
            Site(1, "001", "Site One")
        );
        await MigrateAsync(first);
        var second = await ApplicationAsync(
            ApplicationStatus.Saved,
            Day2,
            Site(1, "001", "Site One"),
            Site(2, "002", "Second Site", isNew: true)
        );

        var result = await MigrateAsync(second);

        Assert.Equal(GroupMigrationOutcome.Migrated, result.Outcome);
        Assert.Equal(1, result.MigratedApplications);
        Assert.Single(
            await AllAsync<RegistrationEntity>(DomainModelCollections.RegistrationVersions)
        );
        Assert.Equal(1, (await CurrentSiteAsync("001")).Version);
        var added = await CurrentSiteAsync("002");
        Assert.Equal(second.Id, added.ProposedByApplicationId);
        Assert.Equal(
            added.EntityId,
            Assert
                .Single(
                    (await CurrentApplicationAsync(second)).ApplicationOverseasSites,
                    e => e.SiteId == 2
                )
                .OverseasSiteId
        );
    }

    [Fact]
    public async Task ConflictingSiteDetails_TheNewestApprovedApplicationWins()
    {
        var approved = await ApplicationForYearAsync(
            2026,
            ApplicationStatus.Approved,
            Day1,
            Site(1, "001", "Approved Name")
        );
        var saved = await ApplicationAsync(
            ApplicationStatus.Saved,
            Day2,
            Site(1, "001", "Proposed Name")
        );

        var result = await MigrateAsync(saved);

        var site = await CurrentSiteAsync("001");
        Assert.Equal("Approved Name", site.SiteName);
        Assert.Equal(OverseasSiteStatus.Active, site.Status);

        var conflict = Assert.Single(
            result.Conflicts,
            c => c.Kind == MigrationConflictKind.OrsDetailsDiffer
        );
        Assert.Equal("001", conflict.OrsId);
        Assert.Equal(approved.Id, conflict.WinnerApplicationId);
        Assert.Equal([saved.Id!.Value], conflict.ApplicationIds);
        Assert.Contains(
            await ConflictsAsync(),
            c => c.Kind == MigrationConflictKind.OrsDetailsDiffer && c.OrsId == "001"
        );

        var proposal = Assert
            .Single((await CurrentApplicationAsync(saved)).ApplicationOverseasSites)
            .ProposedChanges;
        Assert.NotNull(proposal);
        Assert.Equal("Proposed Name", proposal["siteName"].AsString);
        Assert.Null(
            Assert
                .Single((await CurrentApplicationAsync(approved)).ApplicationOverseasSites)
                .ProposedChanges
        );
    }

    [Fact]
    public async Task OperatorSiteOnlyOnRejectedApplications_IsDiscarded()
    {
        var rejected = await ApplicationAsync(
            ApplicationStatus.Withdrawn,
            Day1,
            Site(2, "002", "Gone", isNew: true, Interim(3, "001"))
        );

        await MigrateAsync(rejected);

        var site = await CurrentSiteAsync("002");
        Assert.Equal(OverseasSiteStatus.Discarded, site.Status);
        Assert.Equal(InterimSiteStatus.Discarded, Assert.Single(site.InterimSites).Status);
    }

    [Fact]
    public async Task SiteWithoutAnOrsId_BecomesAProposedSiteWithNoIdAndIsListed()
    {
        var application = await ApplicationAsync(
            ApplicationStatus.Saved,
            Day1,
            Site(1, null, "No Id", isNew: true)
        );

        var result = await MigrateAsync(application);

        var site = await CurrentSiteAsync(null);
        Assert.Equal(OverseasSiteStatus.Proposed, site.Status);
        Assert.Equal(
            site.EntityId,
            Assert
                .Single((await CurrentApplicationAsync(application)).ApplicationOverseasSites)
                .OverseasSiteId
        );
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(MigrationConflictKind.OrsIdMissing, conflict.Kind);
        Assert.Equal([application.Id!.Value], conflict.ApplicationIds);
    }

    [Fact]
    public async Task LegacySingularInterimSiteWithAnOldNumber_IsRenumberedIntoTheRegistrationsPool()
    {
        var legacySite = Site(1, "001", "Site One");
        legacySite.InterimSite = Interim(2, "SN-0001", isNew: false);
        var application = await ApplicationAsync(
            ApplicationStatus.Saved,
            Day1,
            legacySite,
            Site(3, "002", "Site Two", isNew: false, Interim(4, "001", isNew: false))
        );

        var result = await MigrateAsync(application);

        var site = await CurrentSiteAsync("001");
        var interim = Assert.Single(site.InterimSites);
        Assert.Equal("002", interim.SiteNumber);
        Assert.Equal(["SN-0001"], interim.PreviousSiteNumbers);
        Assert.Equal(InterimSiteStatus.Active, interim.Status);
        Assert.Equal(ChangeReason.LegacySiteNumberRenumbered, site.LastChange.Reason);
        Assert.Equal("001", Assert.Single((await CurrentSiteAsync("002")).InterimSites).SiteNumber);

        var entry = Assert.Single(
            (await CurrentApplicationAsync(application)).ApplicationOverseasSites,
            e => e.SiteId == 1
        );
        var interimEntry = Assert.Single(entry.InterimSites);
        Assert.Equal("002", interimEntry.SiteNumber);
        Assert.Equal(2, interimEntry.LegacySiteId);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(MigrationConflictKind.InterimSiteRenumbered, conflict.Kind);
        Assert.Equal("SN-0001", conflict.SiteNumber);
    }

    [Fact]
    public async Task RegistrationAndSitesTheSeedAlreadyCreated_AreKept()
    {
        var registration = await _stores.Registrations.Versions.InsertAsync(
            new RegistrationEntity
            {
                ReExRegistrationId = ReExRegistrationId,
                RegistrationNumber = "R27FROMREEX",
                Source = new EntitySource { System = SourceSystem.ReEx },
            },
            new ChangeInfo { Reason = ChangeReason.ReExImport },
            cancellationToken: Ct
        );
        await _stores.OverseasSites.Versions.InsertAsync(
            new OverseasSiteEntity
            {
                RegistrationId = registration.EntityId,
                OrsId = "001",
                SiteName = "ReEx Name",
                Status = OverseasSiteStatus.Active,
                Origin = SiteOrigin.ReEx,
            },
            new ChangeInfo { Reason = ChangeReason.ReExImport },
            cancellationToken: Ct
        );
        var application = await ApplicationAsync(
            ApplicationStatus.Saved,
            Day1,
            Site(1, "001", "Edited Name")
        );

        var result = await MigrateAsync(application);

        var stored = Assert.Single(
            await AllAsync<RegistrationEntity>(DomainModelCollections.RegistrationVersions)
        );
        Assert.Equal("R27FROMREEX", stored.RegistrationNumber);
        var site = Assert.Single(
            await AllAsync<OverseasSiteEntity>(DomainModelCollections.OverseasSiteVersions)
        );
        Assert.Equal("ReEx Name", site.SiteName);

        var entry = Assert.Single(
            (await CurrentApplicationAsync(application)).ApplicationOverseasSites
        );
        Assert.Equal(site.EntityId, entry.OverseasSiteId);
        Assert.Equal("Edited Name", entry.ProposedChanges!["siteName"].AsString);
        Assert.DoesNotContain(
            result.Conflicts,
            c => c.Kind == MigrationConflictKind.OrsDetailsDiffer
        );
    }

    // One live application per organisation, registration and year is the rule, but Seed only enforces it
    // best-effort (a read-then-create), so legacy data can break it. Such data is linked, never merged, and
    // listed as a defect (§6.3).
    [Fact]
    public async Task DuplicateLiveApplications_AreBothLinkedAndListedAsADefect()
    {
        var first = await ApplicationAsync(
            ApplicationStatus.Saved,
            Day1,
            Site(1, "001", "Site One")
        );
        var second = await ApplicationAsync(
            ApplicationStatus.Submitted,
            Day2,
            Site(1, "001", "Site One")
        );

        var result = await MigrateAsync(first);

        var registration = Assert.Single(
            await AllAsync<RegistrationEntity>(DomainModelCollections.RegistrationVersions)
        );
        Assert.Equal(registration.EntityId, (await CurrentApplicationAsync(first)).RegistrationId);
        Assert.Equal(registration.EntityId, (await CurrentApplicationAsync(second)).RegistrationId);
        var conflict = Assert.Single(
            result.Conflicts,
            c => c.Kind == MigrationConflictKind.DuplicateLiveApplications
        );
        Assert.Equal(
            new[] { first.Id!.Value, second.Id!.Value }.Order(),
            conflict.ApplicationIds.Order()
        );
    }

    [Fact]
    public async Task ApprovedApplication_OnlyItsSelectedSitesAreActivated()
    {
        // As live approval does: a site the approved application did not select is not accredited. One it added is
        // Discarded, and a registered one does not take its details over a newer application that holds it too.
        var approved = NewApplication(
            ApplicationStatus.Approved,
            Day1,
            Site(1, "001", "Approved Edit"),
            Site(2, "002", "Unselected Added", isNew: true),
            Site(3, "003", "Selected Added", isNew: true)
        );
        approved.OverseasSites!.Sites[0].Selected = false;
        approved.OverseasSites.Sites[1].Selected = false;
        approved.AccreditationReference = "A27EX1234567P";
        await _applications.CreateAsync(approved);
        await ApplicationForYearAsync(
            2028,
            ApplicationStatus.Started,
            Day2,
            Site(1, "001", "Newer Name")
        );

        await MigrateAsync(approved);

        Assert.Equal(OverseasSiteStatus.Discarded, (await CurrentSiteAsync("002")).Status);
        Assert.Equal(OverseasSiteStatus.Active, (await CurrentSiteAsync("003")).Status);
        var reEx = await CurrentSiteAsync("001");
        Assert.Equal(OverseasSiteStatus.Active, reEx.Status);
        Assert.Equal("Newer Name", reEx.SiteName);
    }

    [Fact]
    public async Task ApprovedApplication_GetsItsAccreditation()
    {
        var application = NewApplication(
            ApplicationStatus.Approved,
            Day1,
            Site(1, "001", "Site One")
        );
        application.AccreditationReference = "A27EX1234567P";
        application.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo5000;
        application.CaseManagementStatusUpdatedAt = Day2;
        await _applications.CreateAsync(application);

        await MigrateAsync(application);

        var accreditation = Assert.Single(
            await CurrentAsync<AccreditationEntity>(DomainModelCollections.AccreditationVersions)
        );
        Assert.Equal("A27EX1234567P", accreditation.AccreditationNumber);
        Assert.Equal(application.Id, accreditation.ApplicationId);
        Assert.Equal(2027, accreditation.Year);
        Assert.Equal(AccreditationStatus.Approved, accreditation.Status);
        Assert.Equal(TonnageBand.UpTo5000, accreditation.TonnageBand);
        Assert.Equal(Day2, accreditation.ApprovedAt);
        var site = await CurrentSiteAsync("001");
        Assert.Equal(
            site.EntityId,
            Assert.Single(accreditation.AccreditationOverseasSites).OverseasSiteId
        );
        Assert.Equal(
            accreditation.EntityId,
            (await CurrentApplicationAsync(application)).AccreditationId
        );
        Assert.Equal(
            accreditation.EntityId,
            (await LegacyAsync(application))!.AccreditationEntityId
        );
    }

    [Fact]
    public async Task LegacyDocumentWithInt32Enums_IsRead()
    {
        var application = NewApplication(
            ApplicationStatus.Started,
            Day1,
            Site(1, "001", "Site One")
        );
        var document = application.ToBsonDocument();
        document["applicationStatus"] = (int)ApplicationStatus.Started;
        document["prns"]["plannedTonnageBand"] = (int)PlannedTonnageBand.UpTo10000;
        await _factory
            .GetCollection<BsonDocument>("accreditationApplications")
            .InsertOneAsync(document, cancellationToken: Ct);

        await MigrateAsync(application);

        var version = await CurrentApplicationAsync(application);
        Assert.Equal(ApplicationStatus.Started, version.Status);
        Assert.Equal(TonnageBand.UpTo10000, version.Prns.PlannedTonnageBand);
    }

    [Fact]
    public async Task GroupOverTheTransactionLimit_FailsWithoutWriting()
    {
        var applications = Enumerable
            .Range(0, ApplicationGroupMigrator.MaxGroupSize + 1)
            .Select(_ => NewApplication(ApplicationStatus.Withdrawn, Day1))
            .ToList();
        await _factory
            .GetCollection<AccreditationApplicationModel>("accreditationApplications")
            .InsertManyAsync(applications, cancellationToken: Ct);

        var ex = await Assert.ThrowsAsync<ApplicationGroupTooLargeException>(() =>
            MigrateAsync(applications[0])
        );

        Assert.Equal(ReExRegistrationId, ex.ReExRegistrationId);
        Assert.Empty(
            await AllAsync<RegistrationEntity>(DomainModelCollections.RegistrationVersions)
        );
        Assert.Empty(await AllAsync<ApplicationEntity>(DomainModelCollections.ApplicationVersions));
    }

    [Fact]
    public async Task TwoConcurrentFirstTouches_ProduceOneModel()
    {
        var first = await ApplicationAsync(
            ApplicationStatus.Withdrawn,
            Day1,
            Site(1, "001", "Site One"),
            Site(2, "002", "New Site", isNew: true, Interim(3, "001"))
        );
        var second = await ApplicationAsync(
            ApplicationStatus.Saved,
            Day3,
            Site(1, "001", "Site One")
        );

        var results = await Task.WhenAll(MigrateAsync(first), MigrateAsync(second));

        Assert.Single(results, r => r.Outcome == GroupMigrationOutcome.Migrated);
        Assert.Single(await AllAsync<OrganisationEntity>(DomainModelCollections.Organisations));
        Assert.Single(
            await AllAsync<RegistrationEntity>(DomainModelCollections.RegistrationVersions)
        );
        Assert.Equal(
            2,
            (await AllAsync<OverseasSiteEntity>(DomainModelCollections.OverseasSiteVersions)).Count
        );
        Assert.Equal(
            2,
            (await AllAsync<ApplicationEntity>(DomainModelCollections.ApplicationVersions)).Count
        );
    }

    [Fact]
    public async Task UnknownApplication_IsNotFound()
    {
        var result = await MigrateAsync(ObjectId.GenerateNewId());

        Assert.Equal(GroupMigrationOutcome.ApplicationNotFound, result.Outcome);
        Assert.Empty(
            await AllAsync<RegistrationEntity>(DomainModelCollections.RegistrationVersions)
        );
    }
}
