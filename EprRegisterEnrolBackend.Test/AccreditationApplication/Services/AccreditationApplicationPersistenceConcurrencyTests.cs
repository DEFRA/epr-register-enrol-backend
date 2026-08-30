using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

/// <summary>
/// RA-516: proves optimistic concurrency actually prevents a lost update against a real mongod -
/// two callers reading the same document and both attempting a read-modify-replace, where the
/// second writer's stale Version must be rejected rather than silently overwriting the first
/// writer's change.
/// </summary>
public sealed class AccreditationApplicationPersistenceConcurrencyTests : IDisposable
{
    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly AccreditationApplicationPersistence _sut;

    public AccreditationApplicationPersistenceConcurrencyTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("accreditation_concurrency");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _sut = new AccreditationApplicationPersistence(_factory, NullLoggerFactory.Instance);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    [Fact]
    public async Task TwoConcurrentReaders_SecondUpdateIsRejected_FirstWritersChangeIsNotLost()
    {
        var created = await _sut.CreateAsync(
            new AccreditationApplicationModel
            {
                OrganisationId = "org-1",
                Year = 2026,
                MaterialType = MaterialType.Steel,
                ApplicationReference = "initial",
            }
        );
        created.Should().NotBeNull();

        // Two independent readers, both starting from the same stored Version.
        var readerA = await _sut.GetByIdAsync("org-1", created!.Id!.ToString()!);
        var readerB = await _sut.GetByIdAsync("org-1", created.Id!.ToString()!);
        readerA.Should().NotBeNull();
        readerB.Should().NotBeNull();
        readerA!.Version.Should().Be(readerB!.Version);

        readerA.ApplicationReference = "writer-a-change";
        var resultA = await _sut.UpdateAsync(readerA);
        resultA.Should().NotBeNull("the first writer's update has nothing to conflict with");

        // Second writer still holds the pre-update Version - must be rejected, not silently
        // applied over writer A's change.
        readerB.ApplicationReference = "writer-b-change";
        var resultB = await _sut.UpdateAsync(readerB);
        resultB.Should().BeNull("writer B's Version is stale - the document moved on under it");

        var final = await _sut.GetByIdAsync("org-1", created.Id!.ToString()!);
        final.Should().NotBeNull();
        final!
            .ApplicationReference.Should()
            .Be(
                "writer-a-change",
                "writer B's rejected update must not have overwritten writer A's persisted change"
            );
        final
            .Version.Should()
            .Be(readerA.Version, "only the one successful write should have advanced the version");
    }

    /// <summary>
    /// RA-516 review follow-up (masante, discussion_r3888905830): every document written before
    /// this deploy has no "version" field in storage at all - Mongo's equality filter never
    /// matches a genuinely absent field, so without this the very first post-deploy update of a
    /// pre-existing application would find zero matching documents and get rejected as a
    /// permanent, unrecoverable "conflict".
    /// </summary>
    [Fact]
    public async Task PreExistingDocumentWithNoVersionField_CanStillBeUpdated()
    {
        var legacyId = ObjectId.GenerateNewId();
        var rawCollection = _factory.GetCollection<BsonDocument>("accreditationApplications");
        await rawCollection.InsertOneAsync(
            new BsonDocument
            {
                { "_id", legacyId },
                { "organisationId", "org-legacy" },
                { "year", 2025 },
                { "materialType", "Steel" },
                { "applicationStatus", "Saved" },
                { "applicationReference", "legacy-initial" },
                { "dateLastEdited", DateTime.UtcNow },
                { "createdAt", DateTime.UtcNow },
                { "updatedAt", DateTime.UtcNow },
                // Deliberately no "version" field - simulates a document written before RA-516.
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        var legacy = await _sut.GetByIdAsync("org-legacy", legacyId.ToString());
        legacy.Should().NotBeNull();
        legacy!.Version.Should().Be(0, "a missing version field deserializes to the long default");

        legacy.ApplicationReference = "legacy-updated";
        var result = await _sut.UpdateAsync(legacy);

        result
            .Should()
            .NotBeNull(
                "a missing version field must be treated as equivalent to 0, not as a permanent, unrecoverable conflict"
            );
        result!.Version.Should().Be(1);

        var stored = await rawCollection
            .Find(new BsonDocument("_id", legacyId))
            .FirstAsync(TestContext.Current.CancellationToken);
        stored["version"].AsInt64.Should().Be(1);
    }
}
