using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Migration;

/// <summary>
/// R16: one registration can hold hundreds of overseas sites, and a group migrates in one transaction against
/// MongoDB's 60-second limit. Runs the size James set, 1,000 sites on one registration.
/// </summary>
public sealed class ApplicationGroupMigratorVolumeTests : IDisposable
{
    private const string ReExOrganisationId = "6512a0f0c0ffee0000000001";
    private const string ReExRegistrationId = "6512a0f0c0ffee0000000002";
    private const int SiteCount = 1000;
    private const int ApplicationCount = 3;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly AccreditationApplicationPersistence _applications;
    private readonly ApplicationGroupMigrator _migrator;

    public ApplicationGroupMigratorVolumeTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("groupvolume");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        var clock = new TestClock(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        var logs = NullLoggerFactory.Instance;
        _applications = new AccreditationApplicationPersistence(_factory, logs);
        _migrator = new ApplicationGroupMigrator(
            _applications,
            new ApplicationGroupStores(
                new OrganisationPersistence(_factory, logs),
                new RegistrationPersistence(_factory, logs, clock),
                new OverseasSitePersistence(_factory, logs, clock),
                new ApplicationVersionPersistence(_factory, logs, clock),
                new AccreditationPersistence(_factory, logs, clock),
                new MigrationConflictPersistence(_factory, logs)
            ),
            clock
        );
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static OverseasSiteModel Site(int siteId) =>
        new()
        {
            SiteId = siteId,
            OrsId = siteId.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
            SiteName = "Site",
            Country = "France",
            Code1 = "B3011",
            Selected = true,
        };

    private static AccreditationApplicationModel Application(int offset) =>
        new()
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = ReExOrganisationId,
            OrganisationName = "Acme Recycling Ltd",
            RegistrationId = ReExRegistrationId,
            RegistrationReference = "R27EX1234567P",
            IsExporter = true,
            Nation = Nation.England,
            MaterialType = MaterialType.Plastic,
            Year = 2027 + offset,
            ApplicationStatus = ApplicationStatus.Started,
            CreatedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc).AddDays(offset),
            UpdatedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc).AddDays(offset),
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [.. Enumerable.Range(1, SiteCount).Select(Site)],
            },
        };

    [Fact]
    public async Task EnsureMigrated_ARegistrationWith1000Sites_FitsInOneTransaction()
    {
        AccreditationApplicationModel? first = null;
        for (var offset = 0; offset < ApplicationCount; offset++)
        {
            var created = await _applications.CreateAsync(Application(offset));
            first ??= created;
        }

        var result = await InsertRaceRetry.RunInTransactionAsync(
            _factory,
            (session, token) => _migrator.EnsureMigratedAsync(first!.Id!.Value, session, token),
            _ => { },
            Ct
        );

        result.Outcome.Should().Be(GroupMigrationOutcome.Migrated);
        result.MigratedApplications.Should().Be(ApplicationCount);
        (
            await _factory
                .GetCollection<OverseasSiteEntity>(DomainModelCollections.OverseasSiteVersions)
                .CountDocumentsAsync(
                    Builders<OverseasSiteEntity>.Filter.Eq(x => x.IsCurrent, true),
                    cancellationToken: Ct
                )
        )
            .Should()
            .Be(SiteCount);
    }
}
