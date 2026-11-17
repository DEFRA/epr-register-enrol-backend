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
/// RA-706: Application versions written before the model held <c>createdAt</c> get it from the legacy document, as a
/// new version, so the model stands alone once legacy is no longer written (decided 2026-11-15).
/// </summary>
public sealed class ApplicationCreatedAtBackfillTests : IDisposable
{
    private const string ReExOrganisationId = "6512a0f0c0ffee0000000001";
    private const string ReExRegistrationId = "6512a0f0c0ffee0000000002";

    private static readonly DateTime Created = new(2026, 10, 5, 8, 15, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly AccreditationApplicationPersistence _legacy;
    private readonly ApplicationVersionPersistence _applications;
    private readonly ApplicationGroupMigrator _migrator;
    private readonly ApplicationCreatedAtBackfill _backfill;

    public ApplicationCreatedAtBackfillTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("createdatbackfill");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        var clock = new TestClock(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        var logs = NullLoggerFactory.Instance;
        _legacy = new AccreditationApplicationPersistence(_factory, logs);
        _applications = new ApplicationVersionPersistence(_factory, logs, clock);
        _migrator = new ApplicationGroupMigrator(
            _legacy,
            new ApplicationGroupStores(
                new OrganisationPersistence(_factory, logs),
                new RegistrationPersistence(_factory, logs, clock),
                new OverseasSitePersistence(_factory, logs, clock),
                _applications,
                new AccreditationPersistence(_factory, logs, clock),
                new MigrationConflictPersistence(_factory, logs)
            ),
            clock
        );
        _backfill = new ApplicationCreatedAtBackfill(
            _applications,
            _legacy,
            NullLogger<ApplicationCreatedAtBackfill>.Instance
        );
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private IMongoCollection<BsonDocument> Collection(string name) =>
        _factory.GetClient().GetDatabase(_databaseName).GetCollection<BsonDocument>(name);

    // A legacy application migrated into the model, then stripped of createdAt as a version written before RA-706.
    private async Task<AccreditationApplicationModel> MigratedWithoutCreatedAtAsync()
    {
        var legacy = (
            await _legacy.CreateAsync(
                new AccreditationApplicationModel
                {
                    Id = ObjectId.GenerateNewId(),
                    OrganisationId = ReExOrganisationId,
                    OrganisationName = "Acme Recycling Ltd",
                    RegistrationId = ReExRegistrationId,
                    RegistrationReference = "R27EX1234567P",
                    IsExporter = true,
                    Nation = Nation.England,
                    MaterialType = MaterialType.Plastic,
                    Year = 2027,
                    ApplicationStatus = ApplicationStatus.Started,
                    CreatedAt = Created,
                    UpdatedAt = Created,
                    DateLastEdited = Created,
                }
            )
        )!;
        await InsertRaceRetry.RunInTransactionAsync(
            _factory,
            (session, token) => _migrator.EnsureMigratedAsync(legacy.Id!.Value, session, token),
            _ => { },
            Ct
        );
        await Collection(DomainModelCollections.ApplicationVersions)
            .UpdateManyAsync(
                Builders<BsonDocument>.Filter.Eq("entityId", legacy.Id!.Value),
                Builders<BsonDocument>.Update.Unset("createdAt"),
                cancellationToken: Ct
            );
        return legacy;
    }

    private Task<ApplicationEntity?> CurrentAsync(AccreditationApplicationModel legacy) =>
        _applications.Versions.GetCurrentAsync(legacy.Id!.Value, cancellationToken: Ct);

    [Fact]
    public async Task Run_VersionWithoutCreatedAt_WritesANewVersionWithTheLegacyCreatedAt()
    {
        var legacy = await MigratedWithoutCreatedAtAsync();
        var before = (await CurrentAsync(legacy))!;

        var result = await _backfill.RunAsync(Ct);

        Assert.Equal(1, result.Filled);
        var after = (await CurrentAsync(legacy))!;
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(Created, after.CreatedAt);
        Assert.Equal(ChangeReason.CreatedAtBackfilled, after.LastChange.Reason);
    }

    [Fact]
    public async Task Run_VersionAlreadyHoldingCreatedAt_IsLeftAlone()
    {
        var legacy = await MigratedWithoutCreatedAtAsync();
        await _backfill.RunAsync(Ct);
        var filled = (await CurrentAsync(legacy))!;

        var again = await _backfill.RunAsync(Ct);

        Assert.Equal(0, again.Filled);
        Assert.Equal(filled.Version, (await CurrentAsync(legacy))!.Version);
    }

    [Fact]
    public async Task Run_LegacyDocumentGone_SkipsItAndCountsIt()
    {
        var legacy = await MigratedWithoutCreatedAtAsync();
        var before = (await CurrentAsync(legacy))!;
        await Collection("accreditationApplications")
            .DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", legacy.Id!.Value), Ct);

        var result = await _backfill.RunAsync(Ct);

        Assert.Equal(0, result.Filled);
        Assert.Equal(1, result.LegacyMissing);
        var after = (await CurrentAsync(legacy))!;
        Assert.Equal(before.Version, after.Version);
        Assert.Null(after.CreatedAt);
    }

    // Only accreditation applications have a legacy document to take it from.
    [Fact]
    public async Task Run_OtherApplicationTypes_AreLeftAlone()
    {
        var other = await _applications.CreateAsync<TonnageChangeDetails>(
            DomainModelBuilders.Application(
                ObjectId.GenerateNewId(),
                DomainModelBuilders.TonnageChangeDetails()
            ),
            new ChangeInfo { Reason = ChangeReason.ApplicationEdited },
            cancellationToken: Ct
        );

        var result = await _backfill.RunAsync(Ct);

        Assert.Equal(0, result.Filled);
        Assert.Equal(0, result.LegacyMissing);
        var after = (
            await _applications.Versions.GetCurrentAsync(other.EntityId, cancellationToken: Ct)
        )!;
        Assert.Equal(other.Version, after.Version);
    }
}
