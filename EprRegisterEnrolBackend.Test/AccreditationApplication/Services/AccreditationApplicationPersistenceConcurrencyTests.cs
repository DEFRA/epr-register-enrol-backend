using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

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
}
