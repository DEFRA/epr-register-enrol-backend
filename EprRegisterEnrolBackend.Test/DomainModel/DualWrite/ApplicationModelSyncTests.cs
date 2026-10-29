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

    // Migrated first, as the unit of work's ensure-migrated step leaves it before the handler runs.
    private async Task<AccreditationApplicationModel> MigratedApplicationAsync(
        ApplicationStatus status,
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
                    Year = 2027,
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
