using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.Utils.Mongo;

/// <summary>
/// Focused coverage of <see cref="MongoIndexReconciler"/> against a real
/// (ephemeral) Mongo: the empty-model short-circuit, key rendering, leaving an
/// unrelated index untouched while reconciling a changed-options conflict, and
/// skipping a unique index that existing duplicates already violate rather than
/// failing the whole batch (and, with it, service startup).
///
/// Ported alongside <see cref="MongoIndexReconciler"/> from
/// epr-register-enrol-management-be.
/// </summary>
public sealed class MongoIndexReconcilerTests : IDisposable
{
    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly IMongoCollection<AccreditationApplicationModel> _collection;

    public MongoIndexReconcilerTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("reconciler");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _collection = _factory.GetCollection<AccreditationApplicationModel>("accreditationApplications");
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    [Fact]
    public void EnsureIndexes_with_no_models_is_a_no_op()
    {
        var dropped = MongoIndexReconciler.EnsureIndexes(
            _collection,
            Array.Empty<CreateIndexModel<AccreditationApplicationModel>>(),
            NullLogger.Instance);

        Assert.Empty(dropped);
    }

    [Fact]
    public void RenderKeys_renders_each_models_key_specification()
    {
        var models = new List<CreateIndexModel<AccreditationApplicationModel>>
        {
            new(Builders<AccreditationApplicationModel>.IndexKeys.Ascending(a => a.ApplicationReference)),
        };

        var keys = MongoIndexReconciler.RenderKeys(_collection, models);

        var key = Assert.Single(keys);
        Assert.Equal(1, key["applicationReference"].AsInt32);
    }

    [Fact]
    public async Task EnsureIndexes_reconciles_the_conflict_and_leaves_unrelated_indexes_untouched()
    {
        // An unrelated index the desired set does NOT mention must survive the
        // reconcile (exercises the "key not in desired set" branch).
        _collection.Indexes.CreateOne(
            new CreateIndexModel<AccreditationApplicationModel>(
                Builders<AccreditationApplicationModel>.IndexKeys.Ascending(a => a.OrganisationId)),
            cancellationToken: TestContext.Current.CancellationToken);

        // The OLD, non-unique applicationReference index that will conflict.
        _collection.Indexes.CreateOne(
            new CreateIndexModel<AccreditationApplicationModel>(
                Builders<AccreditationApplicationModel>.IndexKeys.Ascending(a => a.ApplicationReference),
                new CreateIndexOptions { Unique = false }),
            cancellationToken: TestContext.Current.CancellationToken);

        var desired = new List<CreateIndexModel<AccreditationApplicationModel>>
        {
            new(
                Builders<AccreditationApplicationModel>.IndexKeys.Ascending(a => a.ApplicationReference),
                new CreateIndexOptions { Unique = true, Sparse = true }),
        };

        var dropped = MongoIndexReconciler.EnsureIndexes(_collection, desired, NullLogger.Instance);

        Assert.Contains(dropped, n => n.Contains("applicationReference"));

        var indexes = await (await _collection.Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(indexes, i => i["key"].AsBsonDocument.Contains("organisationId"));

        var appRef = indexes.Single(i => i["key"].AsBsonDocument.Contains("applicationReference"));
        Assert.True(appRef.GetValue("unique", false).ToBoolean());
        Assert.True(appRef.GetValue("sparse", false).ToBoolean());
    }

    [Fact]
    public async Task EnsureIndexes_skips_a_unique_index_that_existing_duplicates_violate()
    {
        var duplicateDocs = new[]
        {
            new AccreditationApplicationModel
            {
                OrganisationId = "org-1",
                Year = 2026,
                MaterialType = MaterialType.Steel,
                ApplicationReference = "dup-1",
            },
            new AccreditationApplicationModel
            {
                OrganisationId = "org-1",
                Year = 2026,
                MaterialType = MaterialType.Steel,
                ApplicationReference = "dup-1",
            },
        };
        await _collection.InsertManyAsync(duplicateDocs, cancellationToken: TestContext.Current.CancellationToken);

        var desired = new List<CreateIndexModel<AccreditationApplicationModel>>
        {
            new(Builders<AccreditationApplicationModel>.IndexKeys.Ascending(a => a.OrganisationId)),
            new(
                Builders<AccreditationApplicationModel>.IndexKeys.Ascending(a => a.ApplicationReference),
                new CreateIndexOptions { Unique = true, Sparse = true }),
        };

        var dropped = MongoIndexReconciler.EnsureIndexes(_collection, desired, NullLogger.Instance);

        Assert.Empty(dropped);

        var indexes = await (await _collection.Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);

        // The clean index in the batch was still built...
        Assert.Contains(indexes, i => i["key"].AsBsonDocument.Contains("organisationId"));
        // ...but the dirty unique one was skipped rather than crashing startup.
        Assert.DoesNotContain(indexes, i => i["key"].AsBsonDocument.Contains("applicationReference"));
    }
}
