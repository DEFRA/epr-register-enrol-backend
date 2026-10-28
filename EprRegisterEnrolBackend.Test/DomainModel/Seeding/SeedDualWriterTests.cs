using System.Diagnostics.Metrics;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
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

    private SeedDualWriter Writer(IAccreditationApplicationPersistence? applications = null) =>
        new(
            _factory,
            new SeedModelStores(_organisations, _registrations, _overseasSites),
            applications ?? _applications,
            new SeedDualWriteMetrics(_metricsProvider.GetRequiredService<IMeterFactory>()),
            _clock,
            _logger
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

        await Task.WhenAll(applications.Select(a => Writer().WriteAsync(Seed(reEx, a), Ct)));

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
        var entry = Assert.Single(_logger.Entries);
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
