using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.Auth;

/// <summary>
/// Exercises the Mongo-backed <see cref="CaseManagementAuthNonceStore"/> against a
/// real ephemeral mongod (via <see cref="MongoIntegrationFixture"/>) - the atomicity
/// guarantee under test (a unique-index insert either succeeds once or fails with a
/// duplicate-key error) only means anything against a real server, not a mock
/// (epr-register-enrol-backend-0i1).
/// </summary>
public sealed class CaseManagementAuthNonceStoreTests : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly CaseManagementAuthNonceStore _sut;

    public CaseManagementAuthNonceStoreTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("cm_auth_nonces");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _sut = new CaseManagementAuthNonceStore(_factory, NullLoggerFactory.Instance);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    [Fact]
    public async Task TryConsumeAsync_FirstUse_ReturnsTrue()
    {
        var result = await _sut.TryConsumeAsync(
            "nonce-1",
            Ttl,
            TestContext.Current.CancellationToken
        );

        result.Should().BeTrue();
    }

    [Fact]
    public async Task TryConsumeAsync_SameNonceTwice_SecondCallReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _sut.TryConsumeAsync("nonce-2", Ttl, ct);
        var second = await _sut.TryConsumeAsync("nonce-2", Ttl, ct);

        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    // The actual point of this migration: replay protection shared across instances,
    // not just within one process (epr-register-enrol-backend-0i1 acceptance criteria).
    [Fact]
    public async Task TwoStoreInstances_SharedMongo_SecondInstanceSeesFirstsConsumedNonce()
    {
        var ct = TestContext.Current.CancellationToken;
        var otherInstance = new CaseManagementAuthNonceStore(_factory, NullLoggerFactory.Instance);

        var first = await _sut.TryConsumeAsync("nonce-shared", Ttl, ct);
        var second = await otherInstance.TryConsumeAsync("nonce-shared", Ttl, ct);

        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    // Regression coverage for the concurrency guarantee the old static lock provided -
    // proves the unique-index insert is itself the atomic primitive, no external lock
    // needed (see CaseManagementAuthNonceStoreTests's production counterpart removing
    // CaseManagementAuthenticationHandler.NonceLock).
    [Fact]
    public async Task ConcurrentTryConsumeAsync_SameNonce_OnlyOneSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var tasks = Enumerable
            .Range(0, 20)
            .Select(_ => _sut.TryConsumeAsync("nonce-concurrent", Ttl, ct))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r).Should().Be(1);
    }

    [Fact]
    public async Task Constructor_CreatesExpiresAtTtlIndex()
    {
        var collection = _factory.GetCollection<CaseManagementAuthNonceDocument>(
            "caseManagementAuthNonces"
        );
        var indexes = await (
            await collection.Indexes.ListAsync(TestContext.Current.CancellationToken)
        ).ToListAsync(TestContext.Current.CancellationToken);

        var expiresAtIndex = indexes.Single(i => i["key"].AsBsonDocument.Contains("expiresAt"));
        expiresAtIndex.GetValue("expireAfterSeconds", -1).ToInt64().Should().Be(0);
    }
}
