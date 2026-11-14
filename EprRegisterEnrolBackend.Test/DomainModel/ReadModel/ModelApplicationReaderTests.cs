using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using EprRegisterEnrolBackend.Test.DomainModel.Reconciliation;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.ReadModel;

/// <summary>
/// RA-705: with <c>DomainModel:ReadFromModel</c> on, the routes read through <see cref="ModelApplicationReader"/>.
/// It serves the facade's projection, except <c>Version</c>, <c>CreatedAt</c> and <c>UpdatedAt</c>, which still
/// come from the legacy document: <c>Version</c> is the concurrency token every save and Submit check against
/// legacy, and <c>UpdatedAt</c> decides the newest-first order. Legacy owns all three until the cut (decided
/// 2026-11-13).
/// </summary>
public sealed class ModelApplicationReaderTests : IDisposable
{
    private const string LegacyCollection = "accreditationApplications";
    private static readonly DateTime Day1 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly AccreditationApplicationPersistence _applications;
    private readonly ApplicationGroupMigrator _migrator;
    private readonly ApplicationReadModel _readModel;
    private readonly ModelApplicationReader _reader;

    public ModelApplicationReaderTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("model-reader");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        var clock = new TestClock(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        var logs = NullLoggerFactory.Instance;
        _applications = new AccreditationApplicationPersistence(_factory, logs);
        var stores = new ApplicationGroupStores(
            new OrganisationPersistence(_factory, logs),
            new RegistrationPersistence(_factory, logs, clock),
            new OverseasSitePersistence(_factory, logs, clock),
            new ApplicationVersionPersistence(_factory, logs, clock),
            new AccreditationPersistence(_factory, logs, clock),
            new MigrationConflictPersistence(_factory, logs)
        );
        _migrator = new ApplicationGroupMigrator(_applications, stores, clock);
        _readModel = new ApplicationReadModel(stores);
        _reader = new ModelApplicationReader(_factory, _readModel, _applications);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    // The reconciler's builder fills every field the model holds, but with fixed references; ApplicationReference and
    // CaseManagementWorkItemId carry unique indexes, so each application here gets its own.
    private static AccreditationApplicationModel Application(DateTime updatedAt)
    {
        var application = ApplicationReconcilerTests.Application(updatedAt: updatedAt);
        application.ApplicationReference = $"APP-{application.Id}";
        if (application.CaseManagementWorkItemId is not null)
        {
            application.CaseManagementWorkItemId = Guid.NewGuid();
        }

        return application;
    }

    private async Task<AccreditationApplicationModel> CreateLegacyAsync(DateTime updatedAt) =>
        await _applications.CreateAsync(Application(updatedAt: updatedAt))
        ?? throw new InvalidOperationException("The legacy insert failed.");

    private async Task<AccreditationApplicationModel> CreateAndMigrateAsync(DateTime updatedAt)
    {
        var created = await CreateLegacyAsync(updatedAt);
        var id = created.Id!.Value;
        await InsertRaceRetry.RunInTransactionAsync(
            _factory,
            (session, token) => _migrator.EnsureMigratedAsync(id, session, token),
            _ => { },
            Ct
        );
        return (await _applications.GetByIdAsync(created.OrganisationId!, id.ToString()))!;
    }

    private Task SetLegacyAsync(ObjectId id, long version, DateTime updatedAt) =>
        _factory
            .GetCollection<BsonDocument>(LegacyCollection)
            .UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", id),
                Builders<BsonDocument>.Update.Set("version", version).Set("updatedAt", updatedAt),
                cancellationToken: Ct
            );

    private async Task<AccreditationApplicationModel> ProjectionAsync(ObjectId id)
    {
        using var session = await _factory.GetClient().StartSessionAsync(cancellationToken: Ct);
        return (await _readModel.GetAsync(id, session, Ct))!;
    }

    [Fact]
    public async Task GetById_ServesTheProjection()
    {
        var legacy = await CreateAndMigrateAsync(Day1);

        var read = await _reader.GetByIdAsync(
            legacy.OrganisationId!,
            legacy.Id!.Value.ToString(),
            Ct
        );

        read.Should()
            .BeEquivalentTo(
                await ProjectionAsync(legacy.Id!.Value),
                options =>
                    options
                        .Excluding(a => a.Version)
                        .Excluding(a => a.CreatedAt)
                        .Excluding(a => a.UpdatedAt)
            );
    }

    [Fact]
    public async Task GetById_TakesVersionAndTimestampsFromLegacy()
    {
        var legacy = await CreateAndMigrateAsync(Day1);
        var later = Day1.AddDays(3);
        await SetLegacyAsync(legacy.Id!.Value, version: 7, updatedAt: later);

        var read = await _reader.GetByIdAsync(
            legacy.OrganisationId!,
            legacy.Id!.Value.ToString(),
            Ct
        );

        read!.Version.Should().Be(7);
        read.UpdatedAt.Should().Be(later);
        read.CreatedAt.Should().Be(legacy.CreatedAt);
    }

    [Fact]
    public async Task GetById_ForAnotherOrganisation_ReturnsNull()
    {
        var legacy = await CreateAndMigrateAsync(Day1);

        var read = await _reader.GetByIdAsync(
            "6512a0f0c0ffee00000000ff",
            legacy.Id!.Value.ToString(),
            Ct
        );

        read.Should().BeNull();
    }

    [Fact]
    public async Task GetById_WithAnIdThatIsNotAnObjectId_ReturnsNull()
    {
        var legacy = await CreateAndMigrateAsync(Day1);

        var read = await _reader.GetByIdAsync(legacy.OrganisationId!, "not-an-object-id", Ct);

        read.Should().BeNull();
    }

    // Decided 2026-11-13: no fallback to legacy. After the migration everything is in the model, and an application that
    // isn't there should exist in neither, so the route answers 404.
    [Fact]
    public async Task GetById_InLegacyButNotInTheModel_ReturnsNull()
    {
        var legacyOnly = await CreateLegacyAsync(Day1);

        var read = await _reader.GetByIdAsync(
            legacyOnly.OrganisationId!,
            legacyOnly.Id!.Value.ToString(),
            Ct
        );

        read.Should().BeNull();
    }

    [Fact]
    public async Task GetByOrganisation_LeavesOutApplicationsNotInTheModel()
    {
        var migrated = await CreateAndMigrateAsync(Day1);
        await CreateLegacyAsync(Day1.AddDays(2));

        var list = await _reader.GetByOrganisationAsync(migrated.OrganisationId!, Ct);

        list.Select(a => a.Id).Should().Equal(migrated.Id);
    }

    [Fact]
    public async Task GetByOrganisation_KeepsTheLegacyOrderNewestCreatedFirst()
    {
        var older = await CreateAndMigrateAsync(Day1);
        var newer = await CreateAndMigrateAsync(Day1.AddDays(1));
        // Legacy, not the model, decides the order (newest createdAt first): make the first one the newest there.
        await _factory
            .GetCollection<BsonDocument>(LegacyCollection)
            .UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", older.Id!.Value),
                Builders<BsonDocument>.Update.Set("createdAt", newer.CreatedAt.AddDays(5)),
                cancellationToken: Ct
            );

        var list = await _reader.GetByOrganisationAsync(older.OrganisationId!, Ct);

        list.Select(a => a.Id).Should().Equal(older.Id, newer.Id);
        list[0].CreatedAt.Should().Be(newer.CreatedAt.AddDays(5));
    }

    // GetById backfills the legacy document it read and then serves it: ServeAsync turns that legacy document into
    // the response, so the route reads legacy once whichever reader is configured.
    [Fact]
    public async Task Serve_ServesTheProjectionWithVersionAndTimestampsFromTheGivenLegacy()
    {
        var legacy = await CreateAndMigrateAsync(Day1);
        legacy.Version = 9;
        legacy.UpdatedAt = Day1.AddDays(4);

        var served = await _reader.ServeAsync(legacy, Ct);

        served!.Version.Should().Be(9);
        served.UpdatedAt.Should().Be(Day1.AddDays(4));
        served.CreatedAt.Should().Be(legacy.CreatedAt);
        served
            .Should()
            .BeEquivalentTo(
                await ProjectionAsync(legacy.Id!.Value),
                options =>
                    options
                        .Excluding(a => a.Version)
                        .Excluding(a => a.CreatedAt)
                        .Excluding(a => a.UpdatedAt)
            );
    }

    [Fact]
    public async Task Serve_NotInTheModel_ReturnsNull()
    {
        var legacyOnly = await CreateLegacyAsync(Day1);

        var served = await _reader.ServeAsync(legacyOnly, Ct);

        served.Should().BeNull();
    }
}
