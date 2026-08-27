using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Test.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

/// <summary>
/// Proves that constructing <see cref="AccreditationApplicationPersistence"/>
/// actually creates the indexes it declares in
/// <c>DefineIndexes</c> — the behaviour that was silently disabled while
/// <c>MongoService.EnsureIndexes</c> had its <c>CreateMany</c> call commented
/// out (epr-register-enrol-7qn) — and that re-running the constructor against
/// an environment that already carries an older / conflicting copy of an index
/// recovers cleanly instead of crashing startup.
/// </summary>
public sealed class AccreditationApplicationPersistenceMongoIntegrationTests : IDisposable
{
    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly IMongoCollection<AccreditationApplicationModel> _collection;

    public AccreditationApplicationPersistenceMongoIntegrationTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("accreditation_indexes");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _collection = _factory.GetCollection<AccreditationApplicationModel>("accreditationApplications");
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    [Fact]
    public async Task Constructor_creates_the_eight_declared_indexes()
    {
        _ = new AccreditationApplicationPersistence(_factory, NullLoggerFactory.Instance);

        var indexes = await ListIndexesAsync();

        var keyDocs = indexes
            .Where(i => i["name"].AsString != "_id_")
            .Select(i => i["key"].AsBsonDocument.ToString())
            .ToList();

        Assert.Equal(8, keyDocs.Count);

        Assert.Contains(keyDocs, k => k == "{ \"organisationId\" : 1 }");
        Assert.Contains(keyDocs, k => k == "{ \"applicationStatus\" : 1 }");
        Assert.Contains(keyDocs, k => k == "{ \"materialType\" : 1 }");
        Assert.Contains(keyDocs, k => k == "{ \"year\" : 1 }");
        Assert.Contains(keyDocs, k => k == "{ \"sourceReExAccreditationId\" : 1 }");
        Assert.Contains(
            keyDocs,
            k => k.Contains("\"organisationId\" : 1")
                && k.Contains("\"materialType\" : 1")
                && k.Contains("\"year\" : 1"));
        Assert.Contains(keyDocs, k => k == "{ \"applicationReference\" : 1 }");
        Assert.Contains(keyDocs, k => k == "{ \"caseManagementWorkItemId\" : 1 }");

        var appRef = indexes.Single(i => i["key"].AsBsonDocument.Contains("applicationReference"));
        Assert.True(appRef.GetValue("unique", false).ToBoolean());
        Assert.True(appRef.GetValue("sparse", false).ToBoolean());

        var workItemId = indexes.Single(i => i["key"].AsBsonDocument.Contains("caseManagementWorkItemId"));
        Assert.True(workItemId.GetValue("unique", false).ToBoolean());
        Assert.True(workItemId.GetValue("sparse", false).ToBoolean());
    }

    [Fact]
    public void Constructor_is_idempotent_when_the_indexes_already_match()
    {
        _ = new AccreditationApplicationPersistence(_factory, NullLoggerFactory.Instance);

        var ex = Record.Exception(
            () => new AccreditationApplicationPersistence(_factory, NullLoggerFactory.Instance));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Constructor_reconciles_a_pre_existing_non_unique_applicationReference_index()
    {
        // The deploy condition: an environment that already carries a plain,
        // non-unique applicationReference index. CreateMany for the tightened
        // unique+sparse definition raises IndexOptionsConflict; the reconciler
        // must drop + recreate rather than let it crash the constructor.
        _collection.Indexes.CreateOne(
            new CreateIndexModel<AccreditationApplicationModel>(
                Builders<AccreditationApplicationModel>.IndexKeys.Ascending(a => a.ApplicationReference),
                new CreateIndexOptions { Unique = false, Sparse = false }),
            cancellationToken: TestContext.Current.CancellationToken);

        var ex = Record.Exception(
            () => new AccreditationApplicationPersistence(_factory, NullLoggerFactory.Instance));
        Assert.Null(ex);

        var indexes = await ListIndexesAsync();
        var appRef = indexes.Single(i => i["key"].AsBsonDocument.Contains("applicationReference"));
        Assert.True(appRef.GetValue("unique", false).ToBoolean());
        Assert.True(appRef.GetValue("sparse", false).ToBoolean());
    }

    private async Task<List<MongoDB.Bson.BsonDocument>> ListIndexesAsync() =>
        await (await _collection.Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);
}
