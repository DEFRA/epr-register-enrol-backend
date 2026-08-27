using EphemeralMongo;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.TestSupport;

/// <summary>
/// Assembly fixture that boots a single ephemeral <c>mongod</c> instance for
/// the whole test assembly. Tests take a fresh per-test database name from
/// <see cref="NewDatabaseName"/> so collections / indexes from one test do not
/// leak into another, while the (slow) mongod boot is paid once.
///
/// The backend's existing MongoService tests substitute
/// <see cref="IMongoCollection{T}"/> wholesale; this fixture exists so the
/// index-reconciliation path (which only exercises meaningfully against a real
/// server) can be tested the way production actually runs it.
/// </summary>
public sealed class MongoIntegrationFixture : IAsyncLifetime
{
    private IMongoRunner? _runner;

    static MongoIntegrationFixture()
    {
        // Match production startup ordering — the same convention registration
        // Program.cs triggers before any IMongoClient is constructed.
        // Idempotent, so safe even if the fixture is instantiated more than once.
        MongoDbClientFactory.EnsureConventionRegistered();
    }

    public string ConnectionString =>
        _runner?.ConnectionString
        ?? throw new InvalidOperationException("Mongo runner has not started yet.");

    public async ValueTask InitializeAsync()
    {
        _runner = await MongoRunner.RunAsync(new MongoRunnerOptions
        {
            // Single-node replica set is the closest match to the CDP
            // production topology and unlocks transactions / change streams
            // should a future test need them.
            UseSingleNodeReplicaSet = true,
        });
    }

    public ValueTask DisposeAsync()
    {
        _runner?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A unique database name so each test gets a clean slate without paying
    /// for a fresh mongod.
    /// </summary>
    public static string NewDatabaseName(string prefix = "test") =>
        $"{prefix}_{Guid.NewGuid():N}";
}

/// <summary>
/// Minimal <see cref="IMongoDbClientFactory"/> pointing at the fixture's
/// ephemeral <c>mongod</c>, so tests can drive the production persistence
/// constructors without standing up the full DI / Options pipeline.
/// </summary>
public sealed class TestMongoDbClientFactory : IMongoDbClientFactory
{
    private readonly MongoClient _client;
    private readonly IMongoDatabase _database;

    public TestMongoDbClientFactory(string connectionString, string databaseName)
    {
        _client = new MongoClient(connectionString);
        _database = _client.GetDatabase(databaseName);
    }

    public IMongoClient GetClient() => _client;

    public IMongoCollection<T> GetCollection<T>(string collection) =>
        _database.GetCollection<T>(collection);
}
