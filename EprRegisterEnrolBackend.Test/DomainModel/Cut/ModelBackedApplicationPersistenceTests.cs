using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.Cut;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Cut;

/// <summary>
/// RA-706 (decision 6, 2026-11-15): with <c>DomainModel:LegacyWrites</c> off the service reads and writes
/// applications through a model-backed <see cref="IAccreditationApplicationPersistence"/>, so the handlers, seed, API
/// and lookups stay as they are. These tests hold it to the legacy persistence's behaviour, over the same data:
/// each application is created in legacy and migrated, then read both ways.
/// </summary>
public sealed partial class ModelBackedApplicationPersistenceTests : IDisposable
{
    private const string ReExOrganisationId = "6512a0f0c0ffee0000000001";
    private const string ReExRegistrationId = "6512a0f0c0ffee0000000002";

    private static readonly DateTime Created = new(2026, 10, 5, 8, 15, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly string _connectionString;
    private readonly AccreditationApplicationPersistence _legacy;
    private readonly ApplicationGroupStores _stores;
    private readonly ApplicationGroupMigrator _migrator;
    private readonly CutStagingPersistence _staging;
    private readonly ModelBackedApplicationPersistence _model;
    private readonly CapturingLogger<ModelBackedApplicationPersistence> _logger = new();

    public ModelBackedApplicationPersistenceTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("modelbacked");
        _connectionString = fixture.ConnectionString;
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        var clock = new TestClock(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        var logs = NullLoggerFactory.Instance;
        _legacy = new AccreditationApplicationPersistence(_factory, logs);
        _stores = new ApplicationGroupStores(
            new OrganisationPersistence(_factory, logs),
            new RegistrationPersistence(_factory, logs, clock),
            new OverseasSitePersistence(_factory, logs, clock),
            new ApplicationVersionPersistence(_factory, logs, clock),
            new AccreditationPersistence(_factory, logs, clock),
            new MigrationConflictPersistence(_factory, logs)
        );
        _migrator = new ApplicationGroupMigrator(_legacy, _stores, clock);
        _staging = new CutStagingPersistence(_factory, logs);
        // The flush reads through the model-backed persistence, so it is resolved lazily, as the container will.
        _model = new ModelBackedApplicationPersistence(
            _factory,
            _stores,
            new ApplicationReadModel(_stores),
            _staging,
            () => Flush(),
            _logger
        );
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    // --- fixtures -------------------------------------------------------------------------------------------------

    private static OverseasSiteModel Site(int siteId, string orsId) =>
        new()
        {
            SiteId = siteId,
            OrsId = orsId,
            SiteName = "Site " + orsId,
            Country = "France",
            Code1 = "B3011",
        };

    private static AccreditationApplicationModel NewApplication(
        string organisationId = ReExOrganisationId
    ) =>
        new()
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = organisationId,
            OrganisationName = "Acme Recycling Ltd",
            RegistrationId = ReExRegistrationId,
            RegistrationReference = "R27EX1234567P",
            IsExporter = true,
            WasteProcessingType = "exporter",
            Nation = Nation.England,
            MaterialType = MaterialType.Plastic,
            Year = 2027,
            ApplicationStatus = ApplicationStatus.Started,
            CreatedAt = Created,
            UpdatedAt = Created,
            DateLastEdited = Created,
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [Site(1, "001"), Site(2, "002")],
            },
        };

    // Created in legacy, then migrated: the model holds it, and legacy carries the model's foreign keys.
    private async Task<AccreditationApplicationModel> MigratedAsync(
        AccreditationApplicationModel? application = null
    )
    {
        var created = (await _legacy.CreateAsync(application ?? NewApplication()))!;
        await InsertRaceRetry.RunInTransactionAsync(
            _factory,
            (session, token) => _migrator.EnsureMigratedAsync(created.Id!.Value, session, token),
            _ => { },
            Ct
        );
        return (await _legacy.GetByIdAsync(created.OrganisationId, created.Id!.Value.ToString()))!;
    }

    private Task<ApplicationEntity?> CurrentVersionAsync(
        AccreditationApplicationModel application
    ) =>
        _stores.Applications.Versions.GetCurrentAsync(application.Id!.Value, cancellationToken: Ct);

    private async Task<AccreditationApplicationModel?> ReadThroughModelAsync(ObjectId id)
    {
        using var session = await _factory.GetClient().StartSessionAsync(cancellationToken: Ct);
        return await _model.GetByObjectIdAsync(id, session, Ct);
    }

    // The paths at which two documents differ, ignoring those each store keeps for itself.
    private static List<string> Differences(
        AccreditationApplicationModel legacy,
        AccreditationApplicationModel model,
        params string[] ignored
    )
    {
        var found = new List<string>();
        Compare(legacy.ToBsonDocument(), model.ToBsonDocument(), "", found);
        return [.. found.Where(path => !ignored.Contains(path))];
    }

    private static void Compare(BsonValue legacy, BsonValue model, string path, List<string> found)
    {
        if (legacy is BsonDocument l && model is BsonDocument m)
        {
            foreach (var name in l.Names.Union(m.Names))
            {
                Compare(
                    l.GetValue(name, BsonNull.Value),
                    m.GetValue(name, BsonNull.Value),
                    path.Length == 0 ? name : path + "." + name,
                    found
                );
            }
        }
        else if (legacy is BsonArray la && model is BsonArray ma && la.Count == ma.Count)
        {
            for (var i = 0; i < la.Count; i++)
            {
                Compare(la[i], ma[i], $"{path}[{i}]", found);
            }
        }
        else if (legacy != model)
        {
            found.Add(path);
        }
    }

    // --- reading one application ----------------------------------------------------------------------------------

    [Fact]
    public async Task GetByObjectId_MigratedApplication_IsTheLegacyDocumentWithTheModelsBookkeeping()
    {
        var legacy = await MigratedAsync();

        var model = (await ReadThroughModelAsync(legacy.Id!.Value))!;

        // Everything the service reads, the model's foreign keys included (the sync and the registration-number rules
        // use them); only the two stores' own bookkeeping differs, and is checked below.
        Assert.Empty(Differences(legacy, model, "version", "updatedAt"));
    }

    [Fact]
    public async Task GetByObjectId_Version_IsTheModelsVersion()
    {
        var legacy = await MigratedAsync();
        var current = (await CurrentVersionAsync(legacy))!;

        var model = (await ReadThroughModelAsync(legacy.Id!.Value))!;

        Assert.Equal(current.Version, model.Version);
    }

    [Fact]
    public async Task GetByObjectId_UpdatedAt_IsWhenTheCurrentVersionWasWritten()
    {
        var legacy = await MigratedAsync();
        var current = (await CurrentVersionAsync(legacy))!;

        var model = (await ReadThroughModelAsync(legacy.Id!.Value))!;

        Assert.Equal(current.ValidFrom, model.UpdatedAt);
    }

    [Fact]
    public async Task GetByObjectId_NotInTheModel_IsNull()
    {
        var legacyOnly = (await _legacy.CreateAsync(NewApplication()))!;

        Assert.Null(await ReadThroughModelAsync(legacyOnly.Id!.Value));
    }

    [Fact]
    public async Task GetById_OwnOrganisation_ReturnsIt()
    {
        var legacy = await MigratedAsync();

        var model = await _model.GetByIdAsync(ReExOrganisationId, legacy.Id!.Value.ToString());

        Assert.Equal(legacy.Id, model!.Id);
    }

    // As the legacy query does: an application id is only found under its own organisation.
    [Theory]
    [InlineData("6512a0f0c0ffee0000000099")]
    [InlineData("")]
    public async Task GetById_AnotherOrganisation_IsNull(string organisationId)
    {
        var legacy = await MigratedAsync();

        Assert.Null(await _model.GetByIdAsync(organisationId, legacy.Id!.Value.ToString()));
    }

    [Fact]
    public async Task GetById_NotAnObjectId_IsNull()
    {
        await MigratedAsync();

        Assert.Null(await _model.GetByIdAsync(ReExOrganisationId, "not-an-id"));
    }
}
