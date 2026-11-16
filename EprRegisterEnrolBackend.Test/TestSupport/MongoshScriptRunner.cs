using Jint;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.TestSupport;

/// <summary>
/// Runs a QA mongosh script from tools/domain-model under Jint, standing in for mongosh (as
/// LegacyDataSurveyScriptParityTests does for the survey), against a real Mongo, and keeps what it prints. Only the
/// parts of mongosh the scripts use are provided; documents, filters and pipelines cross to the driver as extended JSON.
/// </summary>
public sealed class MongoshScriptRunner(
    TestMongoDbClientFactory factory,
    CancellationToken cancellationToken
)
{
    private const string MongoshShim = """
        class ObjectId {
            constructor(hex) { this.$oid = hex ?? __newId(); }
            toHexString() { return this.$oid; }
        }
        // mongosh sends a Date as a BSON date; extended JSON's $date does the same here.
        Date.prototype.toJSON = function () { return { $date: this.toISOString() }; };
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

    private IMongoCollection<BsonDocument> Collection(string name) =>
        factory.GetCollection<BsonDocument>(name);

    /// <summary>Runs the script at <paramref name="path"/> and returns every line it printed, in order.</summary>
    public List<string> Run(string path)
    {
        var printed = new List<string>();
        var ct = cancellationToken;
        var engine = new Engine(options => options.CancellationToken(ct));
        engine.SetValue("__newId", new Func<string>(() => ObjectId.GenerateNewId().ToString()));
        engine.SetValue(
            "__insert",
            new Action<string, string>(
                (name, docs) =>
                    Collection(name)
                        .InsertMany(
                            BsonSerializer.Deserialize<BsonDocument[]>(docs),
                            cancellationToken: ct
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
                            cancellationToken: ct
                        )
            )
        );
        engine.SetValue(
            "__delete",
            new Action<string, string>(
                (name, filter) =>
                    Collection(name).DeleteMany(BsonDocument.Parse(filter), cancellationToken: ct)
            )
        );
        engine.SetValue(
            "__count",
            new Func<string, string, long>(
                (name, filter) =>
                    Collection(name)
                        .CountDocuments(BsonDocument.Parse(filter), cancellationToken: ct)
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
                            cancellationToken: ct
                        )
                        .ToList(ct);
                    return $"[{string.Join(",", rows.Select(r => r.ToJson(s_relaxed)))}]";
                }
            )
        );
        engine.SetValue("__print", new Action<string>(printed.Add));
        engine.Execute(MongoshShim);
        engine.Execute(File.ReadAllText(path));
        return printed;
    }
}
