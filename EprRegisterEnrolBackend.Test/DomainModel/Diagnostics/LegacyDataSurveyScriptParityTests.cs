using System.Text.Json;
using EprRegisterEnrolBackend.DomainModel.Diagnostics;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Jint;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Diagnostics;

/// <summary>
/// The B1 survey exists twice: <c>tools/domain-model/legacy-data-survey.js</c> runs against production, and
/// <see cref="LegacyDataSurvey"/> runs in test environments. This runs the real script under Jint, standing in for
/// mongosh, against the same data as the C# survey and checks every count agrees, so the two cannot drift apart.
/// </summary>
public sealed class LegacyDataSurveyScriptParityTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string s_scriptPath = Path.Combine(
        AppContext.BaseDirectory,
        "DomainModel",
        "Diagnostics",
        "legacy-data-survey.js"
    );

    // The parts of mongosh the script uses. Filters and pipelines cross to the driver as JSON.
    private const string MongoshShim = """
        const db = {
            getCollection: (name) => ({
                countDocuments: (filter) => __count(name, JSON.stringify(filter)),
                aggregate: (pipeline) => ({
                    toArray: () => JSON.parse(__aggregate(name, JSON.stringify(pipeline))),
                }),
            }),
        };
        const printjson = (value) => __print(JSON.stringify(value));
        """;

    private static readonly JsonWriterSettings s_relaxed = new()
    {
        OutputMode = JsonOutputMode.RelaxedExtendedJson,
    };

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly IMongoCollection<BsonDocument> _applications;

    public LegacyDataSurveyScriptParityTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("surveyparity");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _applications = _factory.GetCollection<BsonDocument>("accreditationApplications");
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    [Fact]
    public async Task Script_EmptyCollection_MatchesTheCSharpSurvey()
    {
        var expected = Counts(await LegacyDataSurvey.RunAsync(_applications, Ct));

        RunScript().Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Script_EveryLegacyShape_MatchesTheCSharpSurvey()
    {
        await _applications.InsertManyAsync(EveryLegacyShape(), cancellationToken: Ct);

        var expected = Counts(await LegacyDataSurvey.RunAsync(_applications, Ct));

        // Every count is non-zero, so agreement cannot come from both sides finding nothing.
        expected.Values.Should().OnlyContain(count => count > 0);
        RunScript().Should().BeEquivalentTo(expected);
    }

    private Dictionary<string, long> RunScript()
    {
        string? printed = null;
        var engine = new Engine(options => options.CancellationToken(Ct));
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
        engine.SetValue("__print", new Action<string>(json => printed = json));
        engine.Execute(MongoshShim);

        // mongosh allows the script's top-level await; wrapping it in an async function lets Jint run it too.
        engine
            .Evaluate($"(async () => {{\n{File.ReadAllText(s_scriptPath)}\n}})()")
            .UnwrapIfPromise();

        printed.Should().NotBeNull("the script prints its report with printjson");
        return JsonSerializer.Deserialize<Dictionary<string, long>>(printed!)!;
    }

    private IMongoCollection<BsonDocument> Collection(string name) =>
        _factory.GetCollection<BsonDocument>(name);

    // The C# report keyed by the names it logs, which are the names the script prints.
    private static Dictionary<string, long> Counts(LegacyDataSurveyReport report) =>
        report
            .ToLogLine()
            .Split(' ')
            .Select(pair => pair.Split('='))
            .ToDictionary(pair => pair[0], pair => long.Parse(pair[1]));

    private static BsonDocument App(params (string Name, BsonValue Value)[] elements)
    {
        var doc = new BsonDocument
        {
            { "organisationId", "org" },
            { "year", 2027 },
            { "materialType", "Plastic" },
            { "applicationStatus", "Submitted" },
        };
        foreach (var (name, value) in elements)
        {
            doc[name] = value;
        }

        return doc;
    }

    private static BsonDocument Sites(params BsonDocument[] sites) =>
        new("sites", new BsonArray(sites));

    // At least one document for every shape the survey counts (the seeds of DomainModelDiagnosticsTests, together).
    private static List<BsonDocument> EveryLegacyShape()
    {
        var singularInterimSite = new BsonDocument
        {
            { "siteId", 1 },
            { "orsId", "001" },
            { "interimSite", new BsonDocument("siteNumber", "001") },
        };
        var singularWithEmptyList = new BsonDocument
        {
            { "siteId", 6 },
            { "orsId", "004" },
            { "interimSite", new BsonDocument("siteNumber", "002") },
            { "interimSites", new BsonArray() },
        };
        var nullOrs = new BsonDocument { { "siteId", 2 }, { "orsId", BsonNull.Value } };
        var missingOrs = new BsonDocument("siteId", 3);
        var snInList = new BsonDocument
        {
            { "siteId", 4 },
            { "orsId", "002" },
            {
                "interimSites",
                new BsonArray { new BsonDocument("siteNumber", "SN-0007") }
            },
        };
        var snSingular = new BsonDocument
        {
            { "siteId", 5 },
            { "orsId", "003" },
            { "interimSite", new BsonDocument("siteNumber", "SN-0008") },
            {
                "interimSites",
                new BsonArray { new BsonDocument("siteNumber", "SN-0008") }
            },
        };

        return
        [
            App(("applicationStatus", 4)),
            App(("materialType", 2)),
            App(("overseasSites", Sites(singularInterimSite))),
            App(("overseasSites", Sites(singularWithEmptyList))),
            App(("overseasSites", Sites(nullOrs, missingOrs))),
            App(("overseasSites", Sites(snInList))),
            App(("overseasSites", Sites(snSingular))),
            App(("registrationId", "r1"), ("registrationReference", "R27EX0000001P")),
            App(("registrationId", "r1"), ("registrationReference", "R27EX0000002P")),
            // A live application next to a withdrawn or rejected one is not a duplicate.
            App(("registrationId", "r2")),
            App(("registrationId", "r2"), ("applicationStatus", "Withdrawn")),
            App(("registrationId", "r5")),
            App(("registrationId", "r5"), ("applicationStatus", "Rejected")),
            // Groups of one, and a group whose values agree, which the grouped counts must not include.
            App(("registrationId", "r3")),
            App(("registrationId", "r4"), ("registrationReference", "R27EX0000003P")),
            App(
                ("registrationId", "r4"),
                ("year", 2026),
                ("registrationReference", "R27EX0000003P")
            ),
            App(("accreditationReference", "A27EX0000002P")),
            App(("applicationStatus", "Approved")),
            App(("applicationStatus", "Approved"), ("accreditationReference", BsonNull.Value)),
            App(("caseManagementReference", "REF-1"), ("accreditationReference", "A27EX0000001P")),
            App(("caseManagementReference", ""), ("accreditationReference", "A27EX0000001P")),
        ];
    }
}
