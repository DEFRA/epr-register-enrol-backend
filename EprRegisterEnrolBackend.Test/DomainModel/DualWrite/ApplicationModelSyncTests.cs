using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Test.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.DualWrite;

/// <summary>
/// B8c: after a write path changes a migrated legacy application, the sync brings the model in line in the same
/// transaction (option B: new sites are Proposed at once, edits to the application's own Proposed sites write
/// through, edits to any other site go to <c>proposedChanges</c>). Real replica set, so transactions are real.
/// </summary>
public sealed class ApplicationModelSyncTests : IDisposable
{
    private const string ReExOrganisationId = "6512a0f0c0ffee0000000001";
    private const string ReExRegistrationId = "6512a0f0c0ffee0000000002";

    private static readonly DateTime Day1 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly AccreditationApplicationPersistence _applications;
    private readonly ApplicationGroupStores _stores;
    private readonly ApplicationGroupMigrator _migrator;
    private readonly ApplicationModelSync _sync;

    public ApplicationModelSyncTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("modelsync");
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
        _sync = new ApplicationModelSync(_applications, _stores);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    // --- fixtures -------------------------------------------------------------------------------------------------

    private static OverseasSiteModel Site(
        int siteId,
        string? orsId,
        string name,
        bool isNew = false
    ) =>
        new()
        {
            SiteId = siteId,
            OrsId = orsId,
            SiteName = name,
            Country = "France",
            Code1 = "B3011",
            IsNewSite = isNew,
        };

    private static InterimSiteModel Interim(
        int siteId,
        string siteNumber,
        string name,
        bool isNew = true
    ) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = siteNumber,
            Country = "Belgium",
            SiteName = name,
            AddressLine1 = "1 Quay",
            TownOrCity = "Antwerp",
            ContactName = "Ann",
            ContactEmail = "ann@example.com",
            ContactPhone = "0123",
            OperationCodes = ["R12"],
            IsNewSite = isNew,
            CreatedAt = Day1,
        };

    private static OverseasSiteModel WithInterims(
        OverseasSiteModel site,
        params InterimSiteModel[] interims
    )
    {
        site.InterimSites = [.. interims];
        InterimSiteSync.SyncMirror(site);
        return site;
    }

    private static InterimSiteEntry InterimEntry(OverseasSiteEntity ors, string siteNumber) =>
        Assert.Single(ors.InterimSites, i => i.SiteNumber == siteNumber);

    private static ApplicationInterimSiteEntry InterimEntry(
        ApplicationEntity application,
        int siteId,
        string siteNumber
    ) => Assert.Single(Entry(application, siteId).InterimSites, i => i.SiteNumber == siteNumber);

    // Migrated first, as the unit of work's ensure-migrated step leaves it before the handler runs.
    private Task<AccreditationApplicationModel> MigratedApplicationAsync(
        ApplicationStatus status,
        params OverseasSiteModel[] sites
    ) => MigratedApplicationAsync(status, 2027, sites);

    private async Task<AccreditationApplicationModel> MigratedApplicationAsync(
        ApplicationStatus status,
        int year,
        params OverseasSiteModel[] sites
    )
    {
        var application = (
            await _applications.CreateAsync(
                new AccreditationApplicationModel
                {
                    Id = ObjectId.GenerateNewId(),
                    OrganisationId = ReExOrganisationId,
                    OrganisationName = "Acme Recycling Ltd",
                    RegistrationId = ReExRegistrationId,
                    IsExporter = true,
                    Nation = Nation.England,
                    MaterialType = MaterialType.Plastic,
                    Year = year,
                    ApplicationStatus = status,
                    CreatedAt = Day1,
                    UpdatedAt = Day1,
                    DateLastEdited = Day1,
                    OverseasSites = new AccreditationApplicationOverseasSites
                    {
                        Sites = [.. sites],
                    },
                }
            )
        )!;
        await InTransactionAsync(
            (session, token) => _migrator.EnsureMigratedAsync(application.Id!.Value, session, token)
        );
        return (await LegacyAsync(application))!;
    }

    // What a handler does: change the legacy application and save it.
    private async Task<AccreditationApplicationModel> EditAsync(
        AccreditationApplicationModel application,
        Action<AccreditationApplicationModel> edit
    )
    {
        var current = (await LegacyAsync(application))!;
        edit(current);
        return (await _applications.UpdateAsync(current))!;
    }

    private Task<T> InTransactionAsync<T>(
        Func<IClientSessionHandle, CancellationToken, Task<T>> work
    ) => InsertRaceRetry.RunInTransactionAsync(_factory, work, _ => { }, Ct);

    private Task<bool> SyncAsync(AccreditationApplicationModel application) =>
        SyncAsync(application.Id!.Value);

    private Task<bool> SyncAsync(ObjectId applicationId) =>
        InTransactionAsync(
            async (session, token) =>
            {
                await _sync.SyncAsync(applicationId, session, token);
                return true;
            }
        );

    private Task<List<T>> AllAsync<T>(string collection, FilterDefinition<T> filter) =>
        _factory.GetCollection<T>(collection).Find(filter).ToListAsync(Ct);

    private async Task<OverseasSiteEntity> CurrentSiteAsync(string orsId) =>
        Assert.Single(
            await AllAsync(
                DomainModelCollections.OverseasSiteVersions,
                Builders<OverseasSiteEntity>.Filter.Eq(x => x.IsCurrent, true)
                    & Builders<OverseasSiteEntity>.Filter.Eq(x => x.OrsId, orsId)
            )
        );

    private async Task<ApplicationEntity> CurrentApplicationAsync(
        AccreditationApplicationModel application
    ) =>
        Assert.Single(
            await AllAsync(
                DomainModelCollections.ApplicationVersions,
                Builders<ApplicationEntity>.Filter.Eq(x => x.IsCurrent, true)
                    & Builders<ApplicationEntity>.Filter.Eq(x => x.EntityId, application.Id!.Value)
            )
        );

    private Task<AccreditationApplicationModel?> LegacyAsync(
        AccreditationApplicationModel application
    ) => _applications.GetByIdAsync(ReExOrganisationId, application.Id.ToString()!);

    private static ApplicationOverseasSiteEntry Entry(ApplicationEntity application, int siteId) =>
        Assert.Single(application.ApplicationOverseasSites, e => e.SiteId == siteId);

    // --- tests ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SiteAdded_CreatesAProposedOrsOwnedByTheApplication()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            Site(1, "001", "ReEx Site")
        );
        await EditAsync(
            application,
            a => a.OverseasSites!.Sites.Add(Site(2, "002", "Added", isNew: true))
        );

        await SyncAsync(application);

        var ors = await CurrentSiteAsync("002");
        Assert.Equal(OverseasSiteStatus.Proposed, ors.Status);
        Assert.Equal(SiteOrigin.Operator, ors.Origin);
        Assert.Equal(application.Id, ors.ProposedByApplicationId);
        Assert.Equal("Added", ors.SiteName);
        Assert.Equal(ChangeReason.SiteProposed, ors.LastChange.Reason);
        Assert.Equal(application.Id, ors.LastChange.ApplicationId);

        var entity = await CurrentApplicationAsync(application);
        Assert.Equal(2, entity.Version);
        Assert.Equal(ChangeReason.ApplicationEdited, entity.LastChange.Reason);
        var entry = Entry(entity, 2);
        Assert.Equal(ors.EntityId, entry.OverseasSiteId);
        Assert.Null(entry.ProposedChanges);

        var legacySite = Assert.Single(
            (await LegacyAsync(application))!.OverseasSites!.Sites,
            s => s.SiteId == 2
        );
        Assert.Equal(ors.EntityId, legacySite.OverseasSiteEntityId);
    }

    [Fact]
    public async Task OwnProposedSiteEdited_WritesThroughToTheOrs()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            Site(1, "002", "Added", isNew: true)
        );
        var before = await CurrentSiteAsync("002");
        Assert.Equal(OverseasSiteStatus.Proposed, before.Status);

        await EditAsync(application, a => a.OverseasSites!.Sites[0].SiteName = "Renamed");
        await SyncAsync(application);

        var ors = await CurrentSiteAsync("002");
        Assert.Equal(before.EntityId, ors.EntityId);
        Assert.Equal(before.Version + 1, ors.Version);
        Assert.Equal("Renamed", ors.SiteName);
        Assert.Equal(OverseasSiteStatus.Proposed, ors.Status);
        Assert.Equal(application.Id, ors.ProposedByApplicationId);
        Assert.Equal(ChangeReason.ProposedSiteEdited, ors.LastChange.Reason);
        Assert.Null(Entry(await CurrentApplicationAsync(application), 1).ProposedChanges);
    }

    [Fact]
    public async Task ActiveSiteEdited_GoesToProposedChangesAndLeavesTheOrsAlone()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            Site(1, "001", "ReEx Site")
        );
        var before = await CurrentSiteAsync("001");
        Assert.Equal(OverseasSiteStatus.Active, before.Status);

        await EditAsync(application, a => a.OverseasSites!.Sites[0].SiteName = "Renamed");
        await SyncAsync(application);

        var ors = await CurrentSiteAsync("001");
        Assert.Equal(before.Version, ors.Version);
        Assert.Equal("ReEx Site", ors.SiteName);
        var changes = Entry(await CurrentApplicationAsync(application), 1).ProposedChanges;
        Assert.NotNull(changes);
        Assert.Equal("Renamed", changes["siteName"].AsString);
    }

    [Fact]
    public async Task ActiveSiteEditUndone_ClearsTheProposedChanges()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            Site(1, "001", "ReEx Site")
        );
        await EditAsync(application, a => a.OverseasSites!.Sites[0].SiteName = "Renamed");
        await SyncAsync(application);

        await EditAsync(application, a => a.OverseasSites!.Sites[0].SiteName = "ReEx Site");
        await SyncAsync(application);

        Assert.Null(Entry(await CurrentApplicationAsync(application), 1).ProposedChanges);
    }

    [Fact]
    public async Task ProposedSiteOfAnotherApplication_IsNotWrittenThrough()
    {
        // A Proposed ORS is only ever held by the application that proposed it; the guard keeps it that way if
        // legacy data says otherwise.
        var proposer = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            Site(1, "002", "Added", isNew: true)
        );
        var before = await CurrentSiteAsync("002");
        before.ProposedByApplicationId = ObjectId.GenerateNewId();
        await InTransactionAsync(
            (session, token) =>
                _stores.OverseasSites.Versions.UpdateAsync(
                    before,
                    before.Version,
                    new ChangeInfo { Reason = ChangeReason.Migration },
                    session,
                    token
                )
        );

        await EditAsync(proposer, a => a.OverseasSites!.Sites[0].SiteName = "Renamed");
        await SyncAsync(proposer);

        var ors = await CurrentSiteAsync("002");
        Assert.Equal("Added", ors.SiteName);
        Assert.Equal(
            "Renamed",
            Entry(await CurrentApplicationAsync(proposer), 1).ProposedChanges!["siteName"].AsString
        );
    }

    [Fact]
    public async Task OwnProposedSiteRemoved_IsDiscarded()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            Site(1, "001", "ReEx Site"),
            Site(2, "002", "Added", isNew: true)
        );

        await EditAsync(application, a => a.OverseasSites!.Sites.RemoveAll(s => s.SiteId == 2));
        await SyncAsync(application);

        var ors = await CurrentSiteAsync("002");
        Assert.Equal(OverseasSiteStatus.Discarded, ors.Status);
        Assert.Equal(ChangeReason.ProposedSiteDiscarded, ors.LastChange.Reason);
        Assert.Equal(OverseasSiteStatus.Active, (await CurrentSiteAsync("001")).Status);
        Assert.DoesNotContain(
            (await CurrentApplicationAsync(application)).ApplicationOverseasSites,
            e => e.SiteId == 2
        );
    }

    [Fact]
    public async Task ApplicationFieldsChanged_WritesANewApplicationVersion()
    {
        var application = await MigratedApplicationAsync(ApplicationStatus.Saved);

        await EditAsync(application, a => a.ApplicationStatus = ApplicationStatus.Started);
        await SyncAsync(application);

        var entity = await CurrentApplicationAsync(application);
        Assert.Equal(2, entity.Version);
        Assert.Equal(ApplicationStatus.Started, entity.Status);
    }

    [Fact]
    public async Task NothingChanged_WritesNoNewVersions()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            Site(1, "001", "ReEx Site"),
            Site(2, "002", "Added", isNew: true)
        );

        await SyncAsync(application);
        await SyncAsync(application);

        Assert.Equal(1, (await CurrentApplicationAsync(application)).Version);
        Assert.Equal(1, (await CurrentSiteAsync("001")).Version);
        Assert.Equal(1, (await CurrentSiteAsync("002")).Version);
    }

    // --- B8d: interim sites ---------------------------------------------------------------------------------------

    [Fact]
    public async Task InterimSiteAdded_AddsAProposedEntryOwnedByTheApplicationToTheOrs()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            Site(1, "001", "ReEx Site")
        );
        var before = await CurrentSiteAsync("001");

        await EditAsync(
            application,
            a => WithInterims(a.OverseasSites!.Sites[0], Interim(2, "001", "Quay"))
        );
        await SyncAsync(application);

        var ors = await CurrentSiteAsync("001");
        Assert.Equal(before.Version + 1, ors.Version);
        Assert.Equal(OverseasSiteStatus.Active, ors.Status);
        var interim = InterimEntry(ors, "001");
        Assert.Equal(InterimSiteStatus.Proposed, interim.Status);
        Assert.Equal(application.Id, interim.ProposedByApplicationId);
        Assert.Equal("Quay", interim.SiteName);
        Assert.Equal(Day1, interim.CreatedAt);
        Assert.Equal(ChangeReason.InterimSiteAdded, ors.LastChange.Reason);
        Assert.Equal("001", ors.LastChange.Details!["siteNumber"]);

        var entry = InterimEntry(await CurrentApplicationAsync(application), 1, "001");
        Assert.Equal(2, entry.LegacySiteId);
        Assert.Null(entry.ProposedChanges);
    }

    [Fact]
    public async Task OwnProposedInterimSiteEdited_WritesThroughToTheOrs()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            WithInterims(Site(1, "001", "ReEx Site"), Interim(2, "001", "Quay"))
        );
        Assert.Equal(
            InterimSiteStatus.Proposed,
            InterimEntry(await CurrentSiteAsync("001"), "001").Status
        );

        await EditAsync(
            application,
            a =>
            {
                a.OverseasSites!.Sites[0].InterimSites[0].SiteName = "Dock";
                InterimSiteSync.SyncMirror(a.OverseasSites.Sites[0]);
            }
        );
        await SyncAsync(application);

        var ors = await CurrentSiteAsync("001");
        var interim = InterimEntry(ors, "001");
        Assert.Equal("Dock", interim.SiteName);
        Assert.Equal(InterimSiteStatus.Proposed, interim.Status);
        Assert.Equal(ChangeReason.InterimSiteUpdated, ors.LastChange.Reason);
        Assert.Null(
            InterimEntry(await CurrentApplicationAsync(application), 1, "001").ProposedChanges
        );
    }

    [Fact]
    public async Task OwnProposedInterimSiteWithdrawnThenRestored_IsWithdrawnThenProposedAgain()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            WithInterims(Site(1, "001", "ReEx Site"), Interim(2, "001", "Quay"))
        );
        var removedAt = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);

        await EditAsync(
            application,
            a => a.OverseasSites!.Sites[0].InterimSites[0].RemovedAt = removedAt
        );
        await SyncAsync(application);

        var withdrawn = await CurrentSiteAsync("001");
        Assert.Equal(InterimSiteStatus.Withdrawn, InterimEntry(withdrawn, "001").Status);
        Assert.Equal(removedAt, InterimEntry(withdrawn, "001").RemovedAt);
        Assert.Equal(ChangeReason.InterimSiteRemoved, withdrawn.LastChange.Reason);

        await EditAsync(
            application,
            a => a.OverseasSites!.Sites[0].InterimSites[0].RemovedAt = null
        );
        await SyncAsync(application);

        var restored = await CurrentSiteAsync("001");
        Assert.Equal(InterimSiteStatus.Proposed, InterimEntry(restored, "001").Status);
        Assert.Null(InterimEntry(restored, "001").RemovedAt);
        Assert.Equal(application.Id, InterimEntry(restored, "001").ProposedByApplicationId);
        Assert.Equal(ChangeReason.InterimSiteReadded, restored.LastChange.Reason);
    }

    [Fact]
    public async Task ActiveInterimSiteEdited_GoesToTheInterimEntrysProposedChanges()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            WithInterims(Site(1, "001", "ReEx Site"), Interim(2, "001", "Quay", isNew: false))
        );
        var before = await CurrentSiteAsync("001");
        Assert.Equal(InterimSiteStatus.Active, InterimEntry(before, "001").Status);

        await EditAsync(
            application,
            a => a.OverseasSites!.Sites[0].InterimSites[0].SiteName = "Dock"
        );
        await SyncAsync(application);

        var ors = await CurrentSiteAsync("001");
        Assert.Equal(before.Version, ors.Version);
        Assert.Equal("Quay", InterimEntry(ors, "001").SiteName);
        var changes = InterimEntry(
            await CurrentApplicationAsync(application),
            1,
            "001"
        ).ProposedChanges;
        Assert.NotNull(changes);
        Assert.Equal("Dock", changes["siteName"].AsString);
        Assert.False(changes.Contains("removedAt"));
    }

    [Fact]
    public async Task ActiveInterimSiteWithdrawnThenRestored_IsAnOverlayThatRestoringClears()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            WithInterims(Site(1, "001", "ReEx Site"), Interim(2, "001", "Quay", isNew: false))
        );
        var before = await CurrentSiteAsync("001");

        await EditAsync(
            application,
            a =>
                a.OverseasSites!.Sites[0].InterimSites[0].RemovedAt = new DateTime(
                    2026,
                    11,
                    2,
                    9,
                    0,
                    0,
                    DateTimeKind.Utc
                )
        );
        await SyncAsync(application);

        var ors = await CurrentSiteAsync("001");
        Assert.Equal(before.Version, ors.Version);
        Assert.Equal(InterimSiteStatus.Active, InterimEntry(ors, "001").Status);
        var changes = InterimEntry(
            await CurrentApplicationAsync(application),
            1,
            "001"
        ).ProposedChanges;
        Assert.NotNull(changes);
        Assert.True(changes.Contains("removedAt"));

        await EditAsync(
            application,
            a => a.OverseasSites!.Sites[0].InterimSites[0].RemovedAt = null
        );
        await SyncAsync(application);

        Assert.Null(
            InterimEntry(await CurrentApplicationAsync(application), 1, "001").ProposedChanges
        );
    }

    [Fact]
    public async Task OwnProposedInterimSiteDropped_IsDiscarded()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            WithInterims(Site(1, "001", "ReEx Site"), Interim(2, "001", "Quay"))
        );

        await EditAsync(application, a => WithInterims(a.OverseasSites!.Sites[0]));
        await SyncAsync(application);

        var ors = await CurrentSiteAsync("001");
        Assert.Equal(InterimSiteStatus.Discarded, InterimEntry(ors, "001").Status);
        Assert.Equal(ChangeReason.ProposedInterimSiteDiscarded, ors.LastChange.Reason);
        Assert.Empty(Entry(await CurrentApplicationAsync(application), 1).InterimSites);
    }

    [Fact]
    public async Task InterimSiteRenumberedByMigration_IsMatchedByItsOldNumberNotAddedAgain()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            WithInterims(Site(1, "001", "ReEx Site"), Interim(2, "SN-0001", "Quay", isNew: false))
        );
        var before = await CurrentSiteAsync("001");
        var renumbered = Assert.Single(before.InterimSites);
        Assert.Contains("SN-0001", renumbered.PreviousSiteNumbers);

        await EditAsync(
            application,
            a => a.OverseasSites!.Sites[0].InterimSites[0].SiteName = "Dock"
        );
        await SyncAsync(application);

        var ors = await CurrentSiteAsync("001");
        Assert.Equal(before.Version, ors.Version);
        Assert.Single(ors.InterimSites);
        var entry = InterimEntry(
            await CurrentApplicationAsync(application),
            1,
            renumbered.SiteNumber
        );
        Assert.Equal("Dock", entry.ProposedChanges!["siteName"].AsString);
    }

    [Fact]
    public async Task InterimSitesUnchanged_WriteNoNewVersions()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Started,
            WithInterims(
                Site(1, "001", "ReEx Site"),
                Interim(2, "001", "Quay"),
                Interim(3, "002", "Pier", isNew: false)
            )
        );

        await SyncAsync(application);

        Assert.Equal(1, (await CurrentApplicationAsync(application)).Version);
        Assert.Equal(1, (await CurrentSiteAsync("001")).Version);
    }

    // --- B8f: rejected and withdrawn ------------------------------------------------------------------------------

    [Theory]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task ApplicationClosed_DiscardsItsProposedSitesAndInterimSites(
        ApplicationStatus closed
    )
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Submitted,
            WithInterims(Site(1, "001", "ReEx Site"), Interim(2, "001", "Quay")),
            Site(3, "002", "Added", isNew: true)
        );

        await EditAsync(application, a => a.ApplicationStatus = closed);
        await SyncAsync(application);

        var proposed = await CurrentSiteAsync("002");
        Assert.Equal(OverseasSiteStatus.Discarded, proposed.Status);
        Assert.Equal(ChangeReason.ProposedSiteDiscarded, proposed.LastChange.Reason);
        var active = await CurrentSiteAsync("001");
        Assert.Equal(OverseasSiteStatus.Active, active.Status);
        Assert.Equal(InterimSiteStatus.Discarded, InterimEntry(active, "001").Status);
        Assert.Equal(ChangeReason.ProposedInterimSiteDiscarded, active.LastChange.Reason);

        // The application still records what it proposed.
        var entity = await CurrentApplicationAsync(application);
        Assert.Equal(closed, entity.Status);
        Assert.Equal(proposed.EntityId, Entry(entity, 3).OverseasSiteId);
        Assert.Equal("001", InterimEntry(entity, 1, "001").SiteNumber);
    }

    [Fact]
    public async Task ApplicationClosed_KeepsItsWithdrawnInterimSiteWithdrawn()
    {
        // As the migration does: a removed interim site is Withdrawn whatever its application's status.
        var withdrawn = Interim(2, "001", "Quay");
        withdrawn.RemovedAt = Day1;
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Submitted,
            WithInterims(Site(1, "001", "ReEx Site"), withdrawn)
        );
        var before = await CurrentSiteAsync("001");
        Assert.Equal(InterimSiteStatus.Withdrawn, InterimEntry(before, "001").Status);

        await EditAsync(application, a => a.ApplicationStatus = ApplicationStatus.Rejected);
        await SyncAsync(application);

        var ors = await CurrentSiteAsync("001");
        Assert.Equal(before.Version, ors.Version);
        Assert.Equal(InterimSiteStatus.Withdrawn, InterimEntry(ors, "001").Status);
    }

    [Fact]
    public async Task ApplicationClosed_ProposalAnotherInFlightApplicationHolds_StaysProposed()
    {
        // As the migration does: a site is Discarded only once no application in flight holds it.
        var closing = await MigratedApplicationAsync(
            ApplicationStatus.Submitted,
            WithInterims(Site(1, "001", "ReEx Site"), Interim(2, "001", "Quay")),
            Site(3, "002", "Added", isNew: true)
        );
        await MigratedApplicationAsync(
            ApplicationStatus.Started,
            2028,
            WithInterims(Site(1, "001", "ReEx Site"), Interim(2, "001", "Quay")),
            Site(3, "002", "Added", isNew: true)
        );
        var proposedBefore = await CurrentSiteAsync("002");
        var activeBefore = await CurrentSiteAsync("001");

        await EditAsync(closing, a => a.ApplicationStatus = ApplicationStatus.Rejected);
        await SyncAsync(closing);

        var proposed = await CurrentSiteAsync("002");
        Assert.Equal(proposedBefore.Version, proposed.Version);
        Assert.Equal(OverseasSiteStatus.Proposed, proposed.Status);
        var active = await CurrentSiteAsync("001");
        Assert.Equal(activeBefore.Version, active.Version);
        Assert.Equal(InterimSiteStatus.Proposed, InterimEntry(active, "001").Status);
    }

    [Fact]
    public async Task ApplicationClosed_SyncedAgain_WritesNoNewVersions()
    {
        var application = await MigratedApplicationAsync(
            ApplicationStatus.Submitted,
            WithInterims(Site(1, "001", "ReEx Site"), Interim(2, "001", "Quay")),
            Site(3, "002", "Added", isNew: true)
        );
        await EditAsync(application, a => a.ApplicationStatus = ApplicationStatus.Rejected);
        await SyncAsync(application);
        var entity = await CurrentApplicationAsync(application);
        var active = await CurrentSiteAsync("001");
        var proposed = await CurrentSiteAsync("002");

        await SyncAsync(application);

        Assert.Equal(entity.Version, (await CurrentApplicationAsync(application)).Version);
        Assert.Null(Entry(await CurrentApplicationAsync(application), 3).ProposedChanges);
        Assert.Equal(active.Version, (await CurrentSiteAsync("001")).Version);
        Assert.Equal(proposed.Version, (await CurrentSiteAsync("002")).Version);
    }

    // --- B9: approval ----------------------------------------------------------------------------------------------

    private const string AccreditationNumber = "A27ER5000270001PL";

    private Task<List<AccreditationEntity>> CurrentAccreditationsAsync() =>
        AllAsync(
            DomainModelCollections.AccreditationVersions,
            Builders<AccreditationEntity>.Filter.Eq(x => x.IsCurrent, true)
        );

    // A submitted application holding a ReEx site (with an interim site it proposed) and a site it added.
    private Task<AccreditationApplicationModel> SubmittedWithProposalsAsync(int year = 2027) =>
        MigratedApplicationAsync(
            ApplicationStatus.Submitted,
            year,
            WithInterims(Site(1, "001", "ReEx Site"), Interim(2, "001", "Quay")),
            Site(3, "002", "Added", isNew: true)
        );

    private Task<AccreditationApplicationModel> ApproveAsync(
        AccreditationApplicationModel application,
        string? accreditationNumber = AccreditationNumber
    ) =>
        EditAsync(
            application,
            a =>
            {
                a.ApplicationStatus = ApplicationStatus.Approved;
                a.AccreditationReference = accreditationNumber;
                a.CaseManagementStatusUpdatedAt = Day1;
            }
        );

    [Fact]
    public async Task ApplicationApproved_ActivatesItsSitesAndCreatesTheAccreditation()
    {
        var application = await SubmittedWithProposalsAsync();
        await EditAsync(application, a => a.OverseasSites!.Sites[0].SiteName = "Renamed");
        await ApproveAsync(application);

        await SyncAsync(application);

        // Its sites take the application's details and become Active, as the migration would leave them.
        var reEx = await CurrentSiteAsync("001");
        Assert.Equal(OverseasSiteStatus.Active, reEx.Status);
        Assert.Equal("Renamed", reEx.SiteName);
        Assert.Equal(InterimSiteStatus.Active, InterimEntry(reEx, "001").Status);
        Assert.Equal(ChangeReason.ApplicationApproved, reEx.LastChange.Reason);
        var added = await CurrentSiteAsync("002");
        Assert.Equal(OverseasSiteStatus.Active, added.Status);
        Assert.Equal(ChangeReason.ApplicationApproved, added.LastChange.Reason);

        var accreditation = Assert.Single(await CurrentAccreditationsAsync());
        Assert.Equal(application.Id, accreditation.ApplicationId);
        Assert.Equal(AccreditationNumber, accreditation.AccreditationNumber);
        Assert.Equal(AccreditationStatus.Approved, accreditation.Status);
        Assert.Equal(Day1, accreditation.ApprovedAt);
        Assert.Equal(ChangeReason.ApplicationApproved, accreditation.LastChange.Reason);
        Assert.Equal(
            [reEx.EntityId, added.EntityId],
            accreditation.AccreditationOverseasSites.Select(s => s.OverseasSiteId)
        );

        var entity = await CurrentApplicationAsync(application);
        Assert.Equal(ApplicationStatus.Approved, entity.Status);
        Assert.Equal(accreditation.EntityId, entity.AccreditationId);
        Assert.Null(Entry(entity, 1).ProposedChanges);
        Assert.Null(InterimEntry(entity, 1, "001").ProposedChanges);
        Assert.Equal(
            accreditation.EntityId,
            (await LegacyAsync(application))!.AccreditationEntityId
        );
    }

    [Fact]
    public async Task ApplicationApproved_SyncedAgain_WritesNothing()
    {
        var application = await SubmittedWithProposalsAsync();
        await ApproveAsync(application);
        await SyncAsync(application);
        var entity = await CurrentApplicationAsync(application);
        var reEx = await CurrentSiteAsync("001");
        var added = await CurrentSiteAsync("002");
        var accreditation = Assert.Single(await CurrentAccreditationsAsync());

        await SyncAsync(application);

        Assert.Equal(entity.Version, (await CurrentApplicationAsync(application)).Version);
        Assert.Equal(reEx.Version, (await CurrentSiteAsync("001")).Version);
        Assert.Equal(added.Version, (await CurrentSiteAsync("002")).Version);
        Assert.Equal(
            accreditation.Version,
            Assert.Single(await CurrentAccreditationsAsync()).Version
        );
    }

    [Fact]
    public async Task ApplicationApprovedWithoutAnAccreditationNumber_FailsAndWritesNothing()
    {
        var application = await SubmittedWithProposalsAsync();
        await ApproveAsync(application, accreditationNumber: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SyncAsync(application));

        Assert.Empty(await CurrentAccreditationsAsync());
        Assert.Equal(OverseasSiteStatus.Proposed, (await CurrentSiteAsync("002")).Status);
    }

    [Fact]
    public async Task RegistrationAlreadyAccreditedForTheYear_FailsTheApproval()
    {
        var first = await SubmittedWithProposalsAsync();
        await ApproveAsync(first);
        await SyncAsync(first);
        var second = await MigratedApplicationAsync(ApplicationStatus.Submitted, 2027);
        await ApproveAsync(second, "A27ER5000270002PL");

        await Assert.ThrowsAsync<InvalidOperationException>(() => SyncAsync(second));

        Assert.Equal(first.Id, Assert.Single(await CurrentAccreditationsAsync()).ApplicationId);
    }

    // --- B8e: registration numbers --------------------------------------------------------------------------------

    private async Task<RegistrationEntity> CurrentRegistrationAsync(
        ApplicationEntity application
    ) =>
        Assert.Single(
            await AllAsync(
                DomainModelCollections.RegistrationVersions,
                Builders<RegistrationEntity>.Filter.Eq(x => x.IsCurrent, true)
                    & Builders<RegistrationEntity>.Filter.Eq(
                        x => x.EntityId,
                        application.RegistrationId!.Value
                    )
            )
        );

    [Fact]
    public async Task RegistrationWithoutANumber_IsIssuedTheApplicationsNumber()
    {
        var application = await MigratedApplicationAsync(ApplicationStatus.Started);
        var before = await CurrentRegistrationAsync(await CurrentApplicationAsync(application));
        Assert.Null(before.RegistrationNumber);

        await EditAsync(application, a => a.RegistrationReference = "R27ER5000270001PL");
        await SyncAsync(application);

        var registration = await CurrentRegistrationAsync(
            await CurrentApplicationAsync(application)
        );
        Assert.Equal(before.Version + 1, registration.Version);
        Assert.Equal("R27ER5000270001PL", registration.RegistrationNumber);
        Assert.Equal(ChangeReason.RegistrationNumberIssued, registration.LastChange.Reason);
        Assert.Equal(application.Id, registration.LastChange.ApplicationId);
    }

    [Fact]
    public async Task RegistrationWithANumber_KeepsItWhateverTheApplicationHolds()
    {
        var application = await MigratedApplicationAsync(ApplicationStatus.Started);
        await EditAsync(application, a => a.RegistrationReference = "R27ER5000270001PL");
        await SyncAsync(application);
        var issued = await CurrentRegistrationAsync(await CurrentApplicationAsync(application));

        await EditAsync(application, a => a.RegistrationReference = "R27ER5000270002PL");
        await SyncAsync(application);

        var registration = await CurrentRegistrationAsync(
            await CurrentApplicationAsync(application)
        );
        Assert.Equal(issued.Version, registration.Version);
        Assert.Equal("R27ER5000270001PL", registration.RegistrationNumber);
    }

    [Fact]
    public async Task ApplicationNotFoundOrNotMigrated_DoesNothing()
    {
        await SyncAsync(ObjectId.GenerateNewId());

        var notMigrated = (
            await _applications.CreateAsync(
                new AccreditationApplicationModel
                {
                    Id = ObjectId.GenerateNewId(),
                    OrganisationId = ReExOrganisationId,
                    RegistrationId = ReExRegistrationId,
                    MaterialType = MaterialType.Plastic,
                    Year = 2027,
                }
            )
        )!;
        await SyncAsync(notMigrated);

        Assert.Empty(
            await AllAsync(
                DomainModelCollections.ApplicationVersions,
                FilterDefinition<ApplicationEntity>.Empty
            )
        );
    }
}
