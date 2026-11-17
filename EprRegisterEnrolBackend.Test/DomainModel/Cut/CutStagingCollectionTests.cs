using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Test.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Cut;

/// <summary>
/// RA-706: with LegacyWrites off a write runs the legacy persistence's own logic (its version check, guard filters and
/// update operators) against a staging copy inside the transaction, and the sync takes the model from the result. The
/// legacy persistence can therefore be pointed at another collection.
/// </summary>
public sealed class CutStagingCollectionTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;

    public CutStagingCollectionTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("cutstaging");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private Task<long> CountAsync(string collection) =>
        _factory
            .GetClient()
            .GetDatabase(_databaseName)
            .GetCollection<BsonDocument>(collection)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

    private static AccreditationApplicationModel Application() =>
        new()
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "6512a0f0c0ffee0000000001",
            RegistrationId = "6512a0f0c0ffee0000000002",
            Year = 2027,
            MaterialType = MaterialType.Plastic,
        };

    [Fact]
    public async Task NamedCollection_WritesThereAndNotToLegacy()
    {
        var staging = new AccreditationApplicationPersistence(
            _factory,
            NullLoggerFactory.Instance,
            DomainModelCollections.CutStagingApplications
        );

        await staging.CreateAsync(Application());

        Assert.Equal(1, await CountAsync(DomainModelCollections.CutStagingApplications));
        Assert.Equal(0, await CountAsync(DomainModelCollections.LegacyApplications));
    }

    [Fact]
    public async Task DefaultCollection_IsStillLegacy()
    {
        var legacy = new AccreditationApplicationPersistence(_factory, NullLoggerFactory.Instance);

        await legacy.CreateAsync(Application());

        Assert.Equal(1, await CountAsync(DomainModelCollections.LegacyApplications));
    }

    // A staged copy must never land in, or be read back from, the collection the cut freezes.
    [Fact]
    public void StagingCollection_IsNotTheLegacyCollection() =>
        Assert.NotEqual(
            DomainModelCollections.LegacyApplications,
            DomainModelCollections.CutStagingApplications
        );
}
