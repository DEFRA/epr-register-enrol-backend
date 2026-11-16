using System.Text.Json;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Jint;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.ReadModel;

/// <summary>
/// RA-705: the read-switch readiness scripts in tools/domain-model are what QA runs with mongosh before
/// <c>DomainModel:ReadFromModel</c> goes on in an environment. This runs the real scripts under Jint, standing in for
/// mongosh (as LegacyDataSurveyScriptParityTests does for the survey), against a real Mongo, so they are known to work.
/// </summary>
public sealed class ReadSwitchReadinessScriptTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Script(string name) =>
        Path.Combine(AppContext.BaseDirectory, "DomainModel", "ReadModel", name);

    // The parts of mongosh the scripts use. Documents, filters and pipelines cross to the driver as extended JSON.
    private const string MongoshShim = """
        class ObjectId {
            constructor(hex) { this.$oid = hex ?? __newId(); }
            toHexString() { return this.$oid; }
        }
        const db = {
            getCollection: (name) => ({
                insertOne: (doc) => __insert(name, JSON.stringify([doc])),
                insertMany: (docs) => __insert(name, JSON.stringify(docs)),
                updateOne: (filter, update) => __update(name, JSON.stringify(filter), JSON.stringify(update)),
                deleteMany: (filter) => __delete(name, JSON.stringify(filter)),
                countDocuments: (filter) => __count(name, JSON.stringify(filter ?? {})),
                aggregate: (pipeline) => ({
                    toArray: () => JSON.parse(__aggregate(name, JSON.stringify(pipeline))),
                }),
            }),
        };
        const print = (text) => __print(String(text));
        const printjson = (value) => __print(JSON.stringify(value));
        """;

    private static readonly JsonWriterSettings s_relaxed = new()
    {
        OutputMode = JsonOutputMode.RelaxedExtendedJson,
    };

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly List<string> _printed = [];

    public ReadSwitchReadinessScriptTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("readiness");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private IMongoCollection<BsonDocument> Collection(string name) =>
        _factory.GetCollection<BsonDocument>(name);

    private void Run(string scriptName)
    {
        var engine = new Engine(options => options.CancellationToken(Ct));
        engine.SetValue("__newId", new Func<string>(() => ObjectId.GenerateNewId().ToString()));
        engine.SetValue(
            "__insert",
            new Action<string, string>(
                (name, docs) =>
                    Collection(name)
                        .InsertMany(
                            BsonSerializer.Deserialize<BsonDocument[]>(docs),
                            cancellationToken: Ct
                        )
            )
        );
        engine.SetValue(
            "__update",
            new Action<string, string, string>(
                (name, filter, update) =>
                    Collection(name)
                        .UpdateOne(
                            BsonDocument.Parse(filter),
                            BsonDocument.Parse(update),
                            cancellationToken: Ct
                        )
            )
        );
        engine.SetValue(
            "__delete",
            new Action<string, string>(
                (name, filter) =>
                    Collection(name).DeleteMany(BsonDocument.Parse(filter), cancellationToken: Ct)
            )
        );
        engine.SetValue(
            "__count",
            new Func<string, string, long>(
                (name, filter) =>
                    Collection(name)
                        .CountDocuments(BsonDocument.Parse(filter), cancellationToken: Ct)
            )
        );
        engine.SetValue(
            "__aggregate",
            new Func<string, string, string>(
                (name, pipeline) =>
                {
                    var stages = BsonSerializer.Deserialize<BsonDocument[]>(pipeline);
                    var rows = Collection(name)
                        .Aggregate(
                            PipelineDefinition<BsonDocument, BsonDocument>.Create(stages),
                            cancellationToken: Ct
                        )
                        .ToList(Ct);
                    return $"[{string.Join(",", rows.Select(r => r.ToJson(s_relaxed)))}]";
                }
            )
        );
        engine.SetValue("__print", new Action<string>(_printed.Add));
        engine.Execute(MongoshShim);
        engine.Execute(File.ReadAllText(Script(scriptName)));
    }

    [Fact]
    public void FixtureTest_EveryCheckFlagsItsCaseAndClearsOnceFixed()
    {
        Run("read-switch-readiness-test.js");

        _printed.Should().NotContain(line => line.StartsWith("FAIL", StringComparison.Ordinal));
        _printed.Last().Should().Be("ALL PASS (9)");
    }

    [Fact]
    public void ReadOnlyChecks_OnAnEmptyDatabase_RunAndFindNothingMissing()
    {
        Run("read-switch-readiness-checks.js");

        var sections = _printed.Where((_, i) => i % 2 == 1).ToList();
        sections.Should().HaveCount(5, "the script prints A, B, C, E and D, each with its result");
        JsonSerializer
            .Deserialize<JsonElement[]>(sections[0])
            .Should()
            .BeEmpty("A: nothing missing");
        JsonSerializer.Deserialize<JsonElement[]>(sections[1]).Should().BeEmpty("B");
        JsonSerializer.Deserialize<JsonElement[]>(sections[2]).Should().BeEmpty("C");
        JsonSerializer.Deserialize<JsonElement[]>(sections[3]).Should().BeEmpty("E");
    }
}
