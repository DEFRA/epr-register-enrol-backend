using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using System.Diagnostics.CodeAnalysis;
using EprRegisterEnrolBackend.Config;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Utils.Mongo;

public interface IMongoDbClientFactory
{
    IMongoClient GetClient();

    IMongoCollection<T> GetCollection<T>(string collection);
}

[ExcludeFromCodeCoverage]
public class MongoDbClientFactory : IMongoDbClientFactory
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly MongoClient _client;

    // ConventionRegistry is a process-global, static registry — registering under the same
    // name twice is not a documented-safe no-op, and this constructor runs once per
    // WebApplicationFactory/DI container. The test suite spins up several of those
    // (AccreditationApplicationTestFactory, ProductionFactory, health-check tests, config
    // binding tests, ...) across parallel xUnit collections, so without this guard multiple
    // threads could call Register("CamelCase", ...) concurrently. That was the actual cause
    // of an intermittent BSON round-trip test failure (OverseasSiteBsonDefaultsTests) that
    // only ever reproduced under CI's CPU-constrained containers, never locally or in
    // isolation — a lock-free race, not a slow test.
    private static readonly object ConventionRegistrationLock = new();
    private static bool _conventionRegistered;

    public MongoDbClientFactory(IOptions<MongoConfig> config)
    {
        var uri = config.Value.DatabaseUri;
        var databaseName = config.Value.DatabaseName;

        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("MongoDB uri string cannot be empty");

        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("MongoDB database name cannot be empty");

        var settings = MongoClientSettings.FromConnectionString(uri);
        _client = new MongoClient(settings);

        // convention must be registered before initialising collection
        EnsureConventionRegistered();

        _mongoDatabase = _client.GetDatabase(databaseName);
    }

    // Internal rather than private so tests whose correctness depends on this convention
    // being active (e.g. OverseasSiteBsonDefaultsTests, which deserialises BsonDocuments by
    // hand) can call it deterministically from a static constructor, rather than relying on
    // some other test class's WebApplicationFactory happening to have run first. Whether a
    // hand-built BsonDocument's elements even need to be camelCase — and whether an unmatched
    // element throws or is silently ignored — depends entirely on whether this convention is
    // registered yet, so "did some other test already trigger this" must never be left to
    // xUnit's test-ordering/parallelisation to decide.
    internal static void EnsureConventionRegistered()
    {
        if (_conventionRegistered)
            return;

        lock (ConventionRegistrationLock)
        {
            if (_conventionRegistered)
                return;

            var camelCaseConvention = new ConventionPack
            {
                new CamelCaseElementNameConvention(),
                new IgnoreExtraElementsConvention(true),
                new IgnoreIfNullConvention(true)
            };
            ConventionRegistry.Register("CamelCase", camelCaseConvention, _ => true);

            _conventionRegistered = true;
        }
    }

    public IMongoCollection<T> GetCollection<T>(string collection)
    {
        return _mongoDatabase.GetCollection<T>(collection);
    }

    public IMongoClient GetClient()
    {
        return _client;
    }
}