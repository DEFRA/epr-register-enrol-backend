using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
    private static AccreditationApplicationModel Application(
        DateTime updatedAt,
        ApplicationStatus status = ApplicationStatus.Queried
    )
    {
        var application = ApplicationReconcilerTests.Application(status, updatedAt);
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

    // GetById backfills the legacy document it read and then serves it: ServeFromSourceAsync turns that legacy document into
    // the response, so the route reads legacy once whichever reader is configured.
    [Fact]
    public async Task Serve_ServesTheProjectionWithVersionAndTimestampsFromTheGivenLegacy()
    {
        var legacy = await CreateAndMigrateAsync(Day1);
        legacy.Version = 9;
        legacy.UpdatedAt = Day1.AddDays(4);

        var served = await _reader.ServeFromSourceAsync(legacy, Ct);

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

        var served = await _reader.ServeFromSourceAsync(legacyOnly, Ct);

        served.Should().BeNull();
    }

    // Golden JSON (design doc phase 5 gate): with ReadFromModel on, the frontend must receive exactly the JSON it receives
    // today. Serialised the way the API serialises (web defaults, camelCase, enums as strings), the served application
    // must match the legacy document field for field, in every status, for an exporter and for a reprocessor.
    private static readonly JsonSerializerOptions s_apiJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static void Differences(JsonNode? legacy, JsonNode? served, string path, List<string> found)
    {
        switch (legacy, served)
        {
            case (JsonObject l, JsonObject m):
                foreach (var key in l.Select(p => p.Key).Union(m.Select(p => p.Key)).Distinct())
                {
                    Differences(l[key], m[key], $"{path}.{key}", found);
                }
                break;
            case (JsonArray l, JsonArray m):
                if (l.Count != m.Count)
                {
                    found.Add($"{path}: {l.Count} items in legacy, {m.Count} served");
                    break;
                }
                for (var i = 0; i < l.Count; i++)
                {
                    Differences(l[i], m[i], $"{path}[{i}]", found);
                }
                break;
            default:
                if (!JsonNode.DeepEquals(legacy, served))
                {
                    found.Add($"{path}: legacy {legacy?.ToJsonString() ?? "null"}, served {served?.ToJsonString() ?? "null"}");
                }
                break;
        }
    }

    public static TheoryData<ApplicationStatus, bool> StatusesAndKinds()
    {
        var data = new TheoryData<ApplicationStatus, bool>();
        foreach (var status in Enum.GetValues<ApplicationStatus>())
        {
            data.Add(status, true);
            data.Add(status, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StatusesAndKinds))]
    public async Task Serve_ProducesTheSameJsonAsLegacy(ApplicationStatus status, bool exporter)
    {
        var application = Application(Day1, status);
        application.IsExporter = exporter;
        application.WasteProcessingType = exporter ? "exporter" : "reprocessor";
        var created = await _applications.CreateAsync(application)
            ?? throw new InvalidOperationException("The legacy insert failed.");
        await InsertRaceRetry.RunInTransactionAsync(
            _factory,
            (session, token) => _migrator.EnsureMigratedAsync(created.Id!.Value, session, token),
            _ => { },
            Ct
        );
        var legacy = (await _applications.GetByIdAsync(created.OrganisationId, created.Id!.Value.ToString()))!;

        var served = await _reader.ServeFromSourceAsync(legacy, Ct);

        served.Should().NotBeNull().And.NotBeSameAs(legacy);
        var found = new List<string>();
        Differences(
            JsonSerializer.SerializeToNode(legacy, s_apiJson),
            JsonSerializer.SerializeToNode(served, s_apiJson),
            "$",
            found
        );
        found.Should().BeEmpty();
    }

    // The golden comparison must be able to fail: a field the model serves differently is reported by its path.
    [Fact]
    public async Task Differences_ReportsAFieldServedDifferently()
    {
        var legacy = await CreateAndMigrateAsync(Day1);
        var served = await _reader.ServeFromSourceAsync(legacy, Ct);
        served!.OrganisationName = "Something Else";
        served.Prns.Authorisers.Clear();

        var found = new List<string>();
        Differences(
            JsonSerializer.SerializeToNode(legacy, s_apiJson),
            JsonSerializer.SerializeToNode(served, s_apiJson),
            "$",
            found
        );

        found.Should().Contain(d => d.StartsWith("$.organisationName:"));
        found.Should().Contain(d => d.StartsWith("$.prns.authorisers:"));
    }

    [Fact]
    public async Task ServeMany_KeepsTheOrderAndLeavesOutApplicationsNotInTheModel()
    {
        // Migrating one application migrates its whole registration group, so the legacy-only one comes last.
        var first = await CreateAndMigrateAsync(Day1);
        var last = await CreateAndMigrateAsync(Day1.AddDays(2));
        var legacyOnly = await CreateLegacyAsync(Day1.AddDays(1));

        var served = await _reader.ServeManyFromSourceAsync([first, legacyOnly, last], Ct);

        served.Select(a => a.Id).Should().Equal(first.Id, last.Id);
    }

    // Captured golden JSON (design doc phase 5 gate). The files under Golden/ hold today's legacy response for each
    // status and kind; legacy and the model must both still produce it, so a change that moved both sides at once
    // fails too. Generated ids are normalised. To re-capture after an intended contract change, run with
    // GOLDEN_RECORD=1 and review the diff of Golden/.
    private static readonly Regex s_generatedIds = new(
        "[0-9a-f]{24}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.Compiled
    );

    private static string GoldenDirectory([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden");

    private static JsonNode Golden(AccreditationApplicationModel application) =>
        JsonNode.Parse(
            s_generatedIds.Replace(JsonSerializer.Serialize(application, s_apiJson), "<generated>")
        )!;

    [Theory]
    [MemberData(nameof(StatusesAndKinds))]
    public async Task Golden_LegacyAndTheModelBothMatchTheCapturedResponse(
        ApplicationStatus status,
        bool exporter
    )
    {
        var application = Application(Day1, status);
        application.IsExporter = exporter;
        application.WasteProcessingType = exporter ? "exporter" : "reprocessor";
        var created =
            await _applications.CreateAsync(application)
            ?? throw new InvalidOperationException("The legacy insert failed.");
        await InsertRaceRetry.RunInTransactionAsync(
            _factory,
            (session, token) => _migrator.EnsureMigratedAsync(created.Id!.Value, session, token),
            _ => { },
            Ct
        );
        var legacy = (
            await _applications.GetByIdAsync(created.OrganisationId, created.Id!.Value.ToString())
        )!;
        var served = (await _reader.ServeFromSourceAsync(legacy, Ct))!;
        var file = Path.Combine(GoldenDirectory(), $"{status}-{(exporter ? "exporter" : "reprocessor")}.json");
        if (Environment.GetEnvironmentVariable("GOLDEN_RECORD") == "1")
        {
            Directory.CreateDirectory(GoldenDirectory());
            await File.WriteAllTextAsync(
                file,
                Golden(legacy).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
                Ct
            );
        }

        var captured = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct));

        var fromLegacy = new List<string>();
        Differences(captured, Golden(legacy), "$", fromLegacy);
        fromLegacy.Should().BeEmpty("legacy must still produce the captured response");
        var fromModel = new List<string>();
        Differences(captured, Golden(served), "$", fromModel);
        fromModel.Should().BeEmpty("the model must produce the captured response");
    }
}
