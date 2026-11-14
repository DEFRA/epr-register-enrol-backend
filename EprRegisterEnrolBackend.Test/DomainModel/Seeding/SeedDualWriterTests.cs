using EprRegisterEnrolBackend.Utils.Mongo;
using System.Diagnostics.Metrics;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Seeding;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Seeding;

/// <summary>Phase 2 seed dual-write against a real single-node replica set, so the transaction is real.</summary>
public sealed class SeedDualWriterTests : IDisposable
{
    private const string ReExOrganisationId = "6512a0f0c0ffee0000000001";
    private const string ReExRegistrationId = "6512a0f0c0ffee0000000002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _readingFromSecondaries;
    private readonly TestClock _clock = new(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
    private readonly OrganisationPersistence _organisations;
    private readonly RegistrationPersistence _registrations;
    private readonly OverseasSitePersistence _overseasSites;
    private readonly AccreditationApplicationPersistence _applications;
    private readonly CapturingLogger<SeedDualWriter> _logger = new();
    private readonly ServiceProvider _metricsProvider;
    private readonly MeterListener _listener = new();
    private long _failures;

    public SeedDualWriterTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("seeddualwrite");
        _readingFromSecondaries = TestMongoDbClientFactory.ReadingFromSecondaries(
            fixture.ConnectionString,
            _databaseName
        );
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _organisations = new OrganisationPersistence(_factory, NullLoggerFactory.Instance);
        _registrations = new RegistrationPersistence(_factory, NullLoggerFactory.Instance, _clock);
        _overseasSites = new OverseasSitePersistence(_factory, NullLoggerFactory.Instance, _clock);
        _applications = new AccreditationApplicationPersistence(
            _factory,
            NullLoggerFactory.Instance
        );

        _metricsProvider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (
                instrument.Meter.Name == SeedDualWriteMetrics.MeterName
                && instrument.Name == SeedDualWriteMetrics.FailuresInstrument
            )
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(
            (_, value, _, _) => Interlocked.Add(ref _failures, value)
        );
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metricsProvider.Dispose();
        _factory.GetClient().DropDatabase(_databaseName);
    }

    // The seed writes the initial Application version through the real migrator (RA-705), over the same database.
    private ApplicationGroupMigrator Migrator(IMongoDbClientFactory factory) =>
        new(
            new AccreditationApplicationPersistence(factory, NullLoggerFactory.Instance),
            new ApplicationGroupStores(
                new OrganisationPersistence(factory, NullLoggerFactory.Instance),
                new RegistrationPersistence(factory, NullLoggerFactory.Instance, _clock),
                new OverseasSitePersistence(factory, NullLoggerFactory.Instance, _clock),
                new ApplicationVersionPersistence(factory, NullLoggerFactory.Instance, _clock),
                new AccreditationPersistence(factory, NullLoggerFactory.Instance, _clock),
                new MigrationConflictPersistence(factory, NullLoggerFactory.Instance)
            ),
            _clock
        );

    // The concurrency test passes its own logger: CapturingLogger is not safe for concurrent writes.
    private SeedDualWriter Writer(ILogger<SeedDualWriter>? logger = null) =>
        new(
            _factory,
            new SeedModelStores(
                _organisations,
                _registrations,
                _overseasSites,
                new MigrationConflictPersistence(_factory, NullLoggerFactory.Instance)
            ),
            Migrator(_factory),
            _applications,
            new SeedDualWriteMetrics(_metricsProvider.GetRequiredService<IMeterFactory>()),
            _clock,
            logger ?? _logger
        );

    private static ReExAccreditationDto ReEx(string siteName = "Site One") =>
        new()
        {
            OrganisationName = "Acme Recycling Ltd",
            RegistrationReference = "R26EX1234567P",
            IsExporter = true,
            Nation = Nation.England,
            OverseasSites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    OrsId = "001",
                    SiteName = siteName,
                    Country = "France",
                    Code1 = "B3011",
                },
                new OverseasSiteModel
                {
                    SiteId = 2,
                    OrsId = "002",
                    SiteName = "Site Two",
                    Country = "Spain",
                },
            ],
        };

    private async Task<AccreditationApplicationModel> LegacyApplicationAsync(
        ReExAccreditationDto reEx
    )
    {
        var application = new AccreditationApplicationModel
        {
            OrganisationId = ReExOrganisationId,
            RegistrationId = ReExRegistrationId,
            MaterialType = MaterialType.Plastic,
            Year = 2027,
            ApplicationStatus = ApplicationStatus.Saved,
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = reEx.OverseasSites,
            },
        };
        return (await _applications.CreateAsync(application))!;
    }

    private static SeedDualWrite Seed(
        ReExAccreditationDto reEx,
        AccreditationApplicationModel application
    ) => new(ReExOrganisationId, ReExRegistrationId, MaterialType.Plastic, reEx, application);

    private Task<List<RegistrationEntity>> RegistrationVersionsAsync() =>
        _factory
            .GetCollection<RegistrationEntity>(DomainModelCollections.RegistrationVersions)
            .Find(FilterDefinition<RegistrationEntity>.Empty)
            .ToListAsync(Ct);

    private Task<List<OverseasSiteEntity>> SiteVersionsAsync() =>
        _factory
            .GetCollection<OverseasSiteEntity>(DomainModelCollections.OverseasSiteVersions)
            .Find(FilterDefinition<OverseasSiteEntity>.Empty)
            .ToListAsync(Ct);

    private Task<List<OrganisationEntity>> OrganisationsAsync() =>
        _factory
            .GetCollection<OrganisationEntity>(DomainModelCollections.Organisations)
            .Find(FilterDefinition<OrganisationEntity>.Empty)
            .ToListAsync(Ct);

    [Fact]
    public async Task WriteAsync_FirstSeed_WritesTheModelAndStampsTheApplication()
    {
        var reEx = ReEx();
        var application = await LegacyApplicationAsync(reEx);

        await Writer().WriteAsync(Seed(reEx, application), Ct);

        var organisation = Assert.Single(await OrganisationsAsync());
        Assert.Equal(ReExOrganisationId, organisation.ReExOrganisationId);
        Assert.Equal("Acme Recycling Ltd", organisation.Name);

        var registration = Assert.Single(await RegistrationVersionsAsync());
        Assert.Equal(organisation.Id, registration.OrganisationId);
        Assert.Equal(ReExRegistrationId, registration.ReExRegistrationId);
        Assert.Equal(1, registration.Version);
        Assert.Equal(ChangeReason.ReExImport, registration.LastChange!.Reason);
        Assert.Equal(application.Id, registration.LastChange.ApplicationId);

        var sites = await SiteVersionsAsync();
        Assert.Equal(["001", "002"], sites.Select(s => s.OrsId).Order());
        Assert.All(sites, s => Assert.Equal(OverseasSiteStatus.Active, s.Status));
        Assert.All(sites, s => Assert.Equal(registration.EntityId, s.RegistrationId));

        var stamped = await _applications.GetByIdAsync(
            ReExOrganisationId,
            application.Id!.Value.ToString()
        );
        Assert.Equal(registration.EntityId, stamped!.RegistrationEntityId);
        Assert.All(
            stamped.OverseasSites!.Sites,
            s =>
                Assert.Equal(sites.Single(e => e.OrsId == s.OrsId).EntityId, s.OverseasSiteEntityId)
        );
        Assert.Equal(application.Version, stamped.Version);
        Assert.Equal(0, Interlocked.Read(ref _failures));
    }

    // The seed's ReEx response, carrying the organisation's number (its public register "Org Id").
    private static ReExAccreditationDto ReExWithOrgId(int? organisationNumber)
    {
        var reEx = ReEx();
        reEx.OrganisationNumber = organisationNumber;
        return reEx;
    }

    private async Task SeedAsync(ReExAccreditationDto reEx) =>
        await Writer().WriteAsync(Seed(reEx, await LegacyApplicationAsync(reEx)), Ct);

    [Fact]
    public async Task WriteAsync_ReExGivesAnOrganisationNumber_SetsItOnTheOrganisation()
    {
        await SeedAsync(ReExWithOrgId(520458));

        Assert.Equal(520458, Assert.Single(await OrganisationsAsync()).OrganisationNumber);
    }

    [Fact]
    public async Task WriteAsync_OrganisationWithoutANumber_IsFilledByALaterSeed()
    {
        await SeedAsync(ReExWithOrgId(null));
        Assert.Null(Assert.Single(await OrganisationsAsync()).OrganisationNumber);

        await SeedAsync(ReExWithOrgId(520458));

        Assert.Equal(520458, Assert.Single(await OrganisationsAsync()).OrganisationNumber);
    }

    [Fact]
    public async Task WriteAsync_OrganisationWithANumber_KeepsIt()
    {
        await SeedAsync(ReExWithOrgId(520458));

        await SeedAsync(ReExWithOrgId(999999));

        Assert.Equal(520458, Assert.Single(await OrganisationsAsync()).OrganisationNumber);
    }

    // The seed's first step inside its transaction is a read, which the driver refuses unless the transaction
    // reads from the primary. In test the CDP connection string sent reads elsewhere and every seed failed.
    [Fact]
    public async Task WriteAsync_ClientReadsFromSecondaries_StillWritesTheModel()
    {
        var reEx = ReEx();
        var application = await LegacyApplicationAsync(reEx);
        var secondaries = _readingFromSecondaries;
        var writer = new SeedDualWriter(
            secondaries,
            new SeedModelStores(
                new OrganisationPersistence(secondaries, NullLoggerFactory.Instance),
                new RegistrationPersistence(secondaries, NullLoggerFactory.Instance, _clock),
                new OverseasSitePersistence(secondaries, NullLoggerFactory.Instance, _clock),
                new MigrationConflictPersistence(secondaries, NullLoggerFactory.Instance)
            ),
            Migrator(secondaries),
            new AccreditationApplicationPersistence(secondaries, NullLoggerFactory.Instance),
            new SeedDualWriteMetrics(_metricsProvider.GetRequiredService<IMeterFactory>()),
            _clock,
            _logger
        );

        await writer.WriteAsync(Seed(reEx, application), Ct);

        Assert.DoesNotContain(_logger.Entries, e => e.LogLevel == LogLevel.Error);
        Assert.Single(await RegistrationVersionsAsync());
        var stamped = await _applications.GetByIdAsync(
            ReExOrganisationId,
            application.Id!.Value.ToString()
        );
        Assert.NotNull(stamped!.RegistrationEntityId);
    }

    [Fact]
    public async Task WriteAsync_SameRegistrationSeededAgainUnchanged_WritesNoNewVersions()
    {
        var reEx = ReEx();
        await Writer().WriteAsync(Seed(reEx, await LegacyApplicationAsync(reEx)), Ct);

        var second = await LegacyApplicationAsync(reEx);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Writer().WriteAsync(Seed(reEx, second), Ct);

        Assert.Single(await RegistrationVersionsAsync());
        Assert.Equal(2, (await SiteVersionsAsync()).Count);
        Assert.Single(await OrganisationsAsync());
        var stamped = await _applications.GetByIdAsync(
            ReExOrganisationId,
            second.Id!.Value.ToString()
        );
        Assert.NotNull(stamped!.RegistrationEntityId);
    }

    [Fact]
    public async Task WriteAsync_SiteChangedInReEx_WritesANewSiteVersionAndKeepsItsStatus()
    {
        await Writer().WriteAsync(Seed(ReEx(), await LegacyApplicationAsync(ReEx())), Ct);
        var original = (await SiteVersionsAsync()).Single(s => s.OrsId == "001");
        await _overseasSites.Versions.UpdateAsync(
            new OverseasSiteEntity
            {
                EntityId = original.EntityId,
                RegistrationId = original.RegistrationId,
                OrsId = original.OrsId,
                SiteName = original.SiteName,
                Country = original.Country,
                BaselOecdCodes = original.BaselOecdCodes,
                Origin = original.Origin,
                Status = OverseasSiteStatus.Discarded,
            },
            original.Version,
            new ChangeInfo { Reason = ChangeReason.Migration },
            cancellationToken: Ct
        );

        _clock.Advance(TimeSpan.FromMinutes(5));
        var renamed = ReEx("Site One Renamed");
        await Writer().WriteAsync(Seed(renamed, await LegacyApplicationAsync(renamed)), Ct);

        var current = await _overseasSites.Versions.GetCurrentAsync(
            original.EntityId,
            cancellationToken: Ct
        );
        Assert.Equal(3, current!.Version);
        Assert.Equal("Site One Renamed", current.SiteName);
        Assert.Equal(OverseasSiteStatus.Discarded, current.Status);
        Assert.Equal(ChangeReason.ReExRefresh, current.LastChange!.Reason);
        Assert.Equal("001", current.LastChange.Details!["orsId"]);
    }

    private async Task<AccreditationApplicationModel> ClosedApplicationAsync(
        ApplicationStatus status,
        bool selected = true
    )
    {
        var reEx = ReEx();
        foreach (var site in reEx.OverseasSites)
        {
            site.Selected = selected;
        }

        var application = await LegacyApplicationAsync(reEx);
        application.ApplicationStatus = status;
        return (await _applications.UpdateAsync(application))!;
    }

    private async Task<OverseasSiteEntity> CurrentSiteAsync(string orsId) =>
        (await SiteVersionsAsync()).Single(s => s.OrsId == orsId && s.IsCurrent);

    private Task<List<MigrationConflict>> ConflictsAsync() =>
        _factory
            .GetCollection<MigrationConflict>(DomainModelCollections.MigrationConflicts)
            .Find(FilterDefinition<MigrationConflict>.Empty)
            .ToListAsync(Ct);

    // What the seed itself records: ReEx disagreeing with an accredited site. Since RA-705 the seed also writes the
    // initial Application version by migrating the application's group, and the migration records its own conflicts
    // about these fixtures (several live applications for one material and year, an approval with no accreditation
    // number). Those are the migrator's tests' business, so the seed's tests look at the seed's own records only.
    private async Task<List<MigrationConflict>> SeedConflictsAsync() =>
        [.. (await ConflictsAsync()).Where(c => c.Kind == MigrationConflictKind.ReExDetailsDiffer)];

    private List<CapturingLogger<SeedDualWriter>.Entry> SeedLogEntries() =>
        [
            .. _logger.Entries.Where(e =>
                !e.Message.StartsWith("Migration conflict recorded:", StringComparison.Ordinal)
                || e.Message.Contains("kind ReExDetailsDiffer", StringComparison.Ordinal)
            ),
        ];

    [Fact]
    public async Task WriteAsync_NumberChangedInReEx_WritesANewVersionKeepingTheOldNumber()
    {
        var first = ReEx();
        await Writer().WriteAsync(Seed(first, await LegacyApplicationAsync(first)), Ct);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var changed = ReEx();
        changed.RegistrationReference = "R27EX1234567P";
        await Writer().WriteAsync(Seed(changed, await LegacyApplicationAsync(changed)), Ct);

        var current = (await RegistrationVersionsAsync()).Single(r => r.IsCurrent);
        Assert.Equal(2, current.Version);
        Assert.Equal(ChangeReason.ReExRefresh, current.LastChange.Reason);
        Assert.Equal("R27EX1234567P", current.RegistrationNumber);
        Assert.Equal(["R26EX1234567P"], current.PreviousRegistrationNumbers);
    }

    [Fact]
    public async Task WriteAsync_SiteOnAnApprovedApplication_IsNotChangedAndTheDifferenceIsRecorded()
    {
        await Writer().WriteAsync(Seed(ReEx(), await LegacyApplicationAsync(ReEx())), Ct);
        await ClosedApplicationAsync(ApplicationStatus.Approved);
        var before = await CurrentSiteAsync("001");

        var renamed = ReEx("Site One Renamed");
        var seeded = await LegacyApplicationAsync(renamed);
        await Writer().WriteAsync(Seed(renamed, seeded), Ct);

        var after = await CurrentSiteAsync("001");
        Assert.Equal(before.Version, after.Version);
        Assert.Equal("Site One", after.SiteName);
        var conflict = Assert.Single(await SeedConflictsAsync());
        Assert.Equal(MigrationConflictKind.ReExDetailsDiffer, conflict.Kind);
        Assert.Equal("001", conflict.OrsId);
        Assert.Equal(ReExRegistrationId, conflict.ReExRegistrationId);
        Assert.Equal(before.RegistrationId, conflict.RegistrationId);
        Assert.Equal(seeded.Id, Assert.Single(conflict.ApplicationIds));
        var stamped = await _applications.GetByIdAsync(
            ReExOrganisationId,
            seeded.Id!.Value.ToString()
        );
        Assert.Equal(
            after.EntityId,
            stamped!.OverseasSites!.Sites.Single(s => s.OrsId == "001").OverseasSiteEntityId
        );
    }

    [Fact]
    public async Task WriteAsync_SiteOnAnApprovedApplication_MatchingReEx_RecordsNoDifference()
    {
        await Writer().WriteAsync(Seed(ReEx(), await LegacyApplicationAsync(ReEx())), Ct);
        await ClosedApplicationAsync(ApplicationStatus.Approved);

        await Writer().WriteAsync(Seed(ReEx(), await LegacyApplicationAsync(ReEx())), Ct);

        Assert.Empty(await SeedConflictsAsync());
    }

    [Fact]
    public async Task WriteAsync_SiteApprovedApplicationDidNotSelect_IsStillRefreshed()
    {
        await Writer().WriteAsync(Seed(ReEx(), await LegacyApplicationAsync(ReEx())), Ct);
        await ClosedApplicationAsync(ApplicationStatus.Approved, selected: false);

        var renamed = ReEx("Site One Renamed");
        await Writer().WriteAsync(Seed(renamed, await LegacyApplicationAsync(renamed)), Ct);

        var after = await CurrentSiteAsync("001");
        Assert.Equal("Site One Renamed", after.SiteName);
        Assert.Equal(ChangeReason.ReExRefresh, after.LastChange.Reason);
        Assert.Empty(await SeedConflictsAsync());
    }

    [Theory]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Submitted)]
    public async Task WriteAsync_SiteOnAnApplicationNotApproved_IsStillRefreshed(
        ApplicationStatus status
    )
    {
        await Writer().WriteAsync(Seed(ReEx(), await LegacyApplicationAsync(ReEx())), Ct);
        await ClosedApplicationAsync(status);

        var renamed = ReEx("Site One Renamed");
        await Writer().WriteAsync(Seed(renamed, await LegacyApplicationAsync(renamed)), Ct);

        Assert.Equal("Site One Renamed", (await CurrentSiteAsync("001")).SiteName);
        Assert.Empty(await SeedConflictsAsync());
    }

    [Fact]
    public async Task WriteAsync_NewSiteOnARegistrationWithAnApprovedApplication_IsCreated()
    {
        await Writer().WriteAsync(Seed(ReEx(), await LegacyApplicationAsync(ReEx())), Ct);
        await ClosedApplicationAsync(ApplicationStatus.Approved);

        var withNewSite = ReEx();
        withNewSite.OverseasSites.Add(
            new OverseasSiteModel
            {
                SiteId = 3,
                OrsId = "003",
                SiteName = "Site Three",
                Country = "Italy",
            }
        );
        await Writer().WriteAsync(Seed(withNewSite, await LegacyApplicationAsync(withNewSite)), Ct);

        Assert.Equal("Site Three", (await CurrentSiteAsync("003")).SiteName);
    }

    [Fact]
    public async Task WriteAsync_ConcurrentSeedsOfOneRegistration_WriteOneModelAndStampBoth()
    {
        var reEx = ReEx();
        var applications = new[]
        {
            await LegacyApplicationAsync(reEx),
            await LegacyApplicationAsync(reEx),
            await LegacyApplicationAsync(reEx),
        };

        await Task.WhenAll(
            applications.Select(a =>
                Writer(NullLogger<SeedDualWriter>.Instance).WriteAsync(Seed(reEx, a), Ct)
            )
        );

        Assert.Equal(0, Interlocked.Read(ref _failures));
        var registration = Assert.Single(await RegistrationVersionsAsync());
        Assert.Equal(2, (await SiteVersionsAsync()).Count);
        Assert.Single(await OrganisationsAsync());
        foreach (var application in applications)
        {
            var stamped = await _applications.GetByIdAsync(
                ReExOrganisationId,
                application.Id!.Value.ToString()
            );
            Assert.Equal(registration.EntityId, stamped!.RegistrationEntityId);
        }
    }

    [Fact]
    public async Task WriteAsync_NumberChangedInReEx_LogsAWarningWithBothNumbers()
    {
        var first = ReEx();
        await Writer().WriteAsync(Seed(first, await LegacyApplicationAsync(first)), Ct);
        Assert.Empty(SeedLogEntries());

        var changed = ReEx();
        changed.RegistrationReference = "R27EX1234567P";
        var seeded = await LegacyApplicationAsync(changed);
        await Writer().WriteAsync(Seed(changed, seeded), Ct);

        var entry = Assert.Single(SeedLogEntries());
        Assert.Equal(LogLevel.Warning, entry.LogLevel);
        Assert.Contains(ReExRegistrationId, entry.Message);
        Assert.Contains("R26EX1234567P", entry.Message);
        Assert.Contains("R27EX1234567P", entry.Message);
        Assert.Contains(seeded.Id.ToString()!, entry.Message);
        Assert.Empty(await SeedConflictsAsync());
    }

    [Fact]
    public async Task WriteAsync_NumberUnchanged_LogsNoWarning()
    {
        var reEx = ReEx();
        await Writer().WriteAsync(Seed(reEx, await LegacyApplicationAsync(reEx)), Ct);

        await Writer().WriteAsync(Seed(reEx, await LegacyApplicationAsync(reEx)), Ct);

        Assert.Empty(SeedLogEntries());
    }

    [Fact]
    public async Task WriteAsync_RolledBackAfterANumberChange_LogsOnlyTheFailure()
    {
        var first = ReEx();
        await Writer().WriteAsync(Seed(first, await LegacyApplicationAsync(first)), Ct);
        var changed = ReEx();
        changed.RegistrationReference = "R27EX1234567P";
        // The application is never inserted, so stamping it fails after the number changed.
        var missing = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = ReExOrganisationId,
            RegistrationId = ReExRegistrationId,
            MaterialType = MaterialType.Plastic,
            Year = 2027,
        };

        await Writer().WriteAsync(Seed(changed, missing), Ct);

        var current = (await RegistrationVersionsAsync()).Single(r => r.IsCurrent);
        Assert.Equal("R26EX1234567P", current.RegistrationNumber);
        Assert.Equal(LogLevel.Error, Assert.Single(SeedLogEntries()).LogLevel);
    }

    [Fact]
    public async Task WriteAsync_ReExDiffersFromAnAccreditedSite_LogsTheConflictWithoutPersonalData()
    {
        await Writer().WriteAsync(Seed(ReEx(), await LegacyApplicationAsync(ReEx())), Ct);
        await ClosedApplicationAsync(ApplicationStatus.Approved);

        var renamed = ReEx("Site One Renamed");
        var seeded = await LegacyApplicationAsync(renamed);
        await Writer().WriteAsync(Seed(renamed, seeded), Ct);

        var entry = Assert.Single(SeedLogEntries());
        Assert.Equal(LogLevel.Warning, entry.LogLevel);
        Assert.Contains(nameof(MigrationConflictKind.ReExDetailsDiffer), entry.Message);
        Assert.Contains(ReExRegistrationId, entry.Message);
        Assert.Contains("001", entry.Message);
        Assert.Contains(seeded.Id.ToString()!, entry.Message);
        foreach (var personal in new[] { "Acme", "Site One", "France" })
        {
            Assert.DoesNotContain(personal, entry.Message);
        }
    }

    [Fact]
    public async Task WriteAsync_AnAccreditedSiteAlreadyHoldingADate_KeepsIt()
    {
        var first = ReEx();
        first.OverseasSites[0].ValidFrom = "2019-03-01T00:00:00Z";
        await Writer().WriteAsync(Seed(first, await LegacyApplicationAsync(first)), Ct);
        await ClosedApplicationAsync(ApplicationStatus.Approved);

        var later = ReEx();
        later.OverseasSites[0].ValidFrom = "2025-01-01T00:00:00Z";
        await Writer().WriteAsync(Seed(later, await LegacyApplicationAsync(later)), Ct);

        var site = await _factory
            .GetCollection<OverseasSiteEntity>(DomainModelCollections.OverseasSiteVersions)
            .Find(
                Builders<OverseasSiteEntity>.Filter.Eq(s => s.OrsId, "001")
                    & Builders<OverseasSiteEntity>.Filter.Eq(s => s.IsCurrent, true)
            )
            .SingleAsync(Ct);
        Assert.Equal(new DateTime(2019, 3, 1, 0, 0, 0, DateTimeKind.Utc), site.FirstApprovedAt);
    }

    [Fact]
    public async Task WriteAsync_RolledBackAfterAConflict_LogsOnlyTheFailure()
    {
        await Writer().WriteAsync(Seed(ReEx(), await LegacyApplicationAsync(ReEx())), Ct);
        await ClosedApplicationAsync(ApplicationStatus.Approved);
        // The application is never inserted, so stamping it fails after the conflict was recorded.
        var missing = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = ReExOrganisationId,
            RegistrationId = ReExRegistrationId,
            MaterialType = MaterialType.Plastic,
            Year = 2027,
        };

        await Writer().WriteAsync(Seed(ReEx("Site One Renamed"), missing), Ct);

        Assert.Empty(await SeedConflictsAsync());
        Assert.Equal(LogLevel.Error, Assert.Single(SeedLogEntries()).LogLevel);
    }

    [Fact]
    public async Task WriteAsync_GroupOverTheSizeLimit_RollsBackAndLeavesAnAccreditedSiteAlone()
    {
        var original = ReEx();
        await Writer().WriteAsync(Seed(original, await LegacyApplicationAsync(original)), Ct);
        for (var i = 0; i < ApplicationGroupMigrator.MaxGroupSize; i++)
        {
            await LegacyApplicationAsync(original);
        }

        // Inserted last, so the capped read would miss it if the count went unchecked.
        await ClosedApplicationAsync(ApplicationStatus.Approved);
        var before = await CurrentSiteAsync("001");

        var renamed = ReEx("Site One Renamed");
        var seeded = await LegacyApplicationAsync(renamed);
        await Writer().WriteAsync(Seed(renamed, seeded), Ct);

        var after = await CurrentSiteAsync("001");
        Assert.Equal(before.Version, after.Version);
        Assert.Equal("Site One", after.SiteName);
        Assert.Empty(await SeedConflictsAsync());
        Assert.Equal(1, Interlocked.Read(ref _failures));
        var entry = Assert.Single(SeedLogEntries());
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.Contains(seeded.Id.ToString()!, entry.Message);
        Assert.Contains(
            $"{nameof(ApplicationGroupTooLargeException)} at SeedDualWriter.AccreditedOrsIdsAsync",
            entry.Message
        );
    }

    [Fact]
    public async Task WriteAsync_GroupAtTheSizeLimit_StillWrites()
    {
        var original = ReEx();
        for (var i = 0; i < ApplicationGroupMigrator.MaxGroupSize - 1; i++)
        {
            await LegacyApplicationAsync(original);
        }

        await Writer().WriteAsync(Seed(original, await LegacyApplicationAsync(original)), Ct);

        Assert.Equal(0, Interlocked.Read(ref _failures));
        Assert.Equal("Site One", (await CurrentSiteAsync("001")).SiteName);
    }

    [Fact]
    public async Task WriteAsync_ModelWriteFails_RollsBackLogsWithoutPersonalDataAndDoesNotThrow()
    {
        var reEx = ReEx();
        reEx.SubmitterContactDetails = new SubmitterContactDetailsDto
        {
            FullName = "Jane Example",
            Email = "jane@example.com",
        };
        // The application is never inserted, so stamping it fails after the entities were written.
        var missing = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = ReExOrganisationId,
            RegistrationId = ReExRegistrationId,
            MaterialType = MaterialType.Plastic,
            Year = 2027,
        };

        await Writer().WriteAsync(Seed(reEx, missing), Ct);

        Assert.Empty(await OrganisationsAsync());
        Assert.Empty(await RegistrationVersionsAsync());
        Assert.Empty(await SiteVersionsAsync());
        Assert.Equal(1, Interlocked.Read(ref _failures));
        var entry = Assert.Single(SeedLogEntries());
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.Null(entry.Exception);
        Assert.Contains(missing.Id.ToString()!, entry.Message);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);
        foreach (var personal in new[] { "Acme", "Jane", "jane@example.com", "Site One", "France" })
        {
            Assert.DoesNotContain(personal, entry.Message);
        }
    }
}
