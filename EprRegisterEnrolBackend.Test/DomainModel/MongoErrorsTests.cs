using System.Net;
using EprRegisterEnrolBackend.DomainModel;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;

namespace EprRegisterEnrolBackend.Test.DomainModel;

/// <summary>
/// RA-644: only a duplicate key is an insert race the unit of work retries; every other Mongo error fails the
/// request at once. A real duplicate-key <see cref="MongoWriteException"/> is covered against a replica set in
/// DualWriteUnitOfWorkTests; these cover the command form and the errors that must not count.
/// </summary>
public class MongoErrorsTests
{
    private static MongoCommandException CommandError(int code) =>
        new(
            new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            "command failed",
            new BsonDocument("insert", "x"),
            new BsonDocument { { "ok", 0 }, { "code", code } }
        );

    [Theory]
    [InlineData(11000, true)]
    [InlineData(112, false)]
    [InlineData(50, false)]
    public void IsDuplicateKey_CommandError_OnlyForCode11000(int code, bool expected) =>
        Assert.Equal(expected, MongoErrors.IsDuplicateKey(CommandError(code)));

    [Fact]
    public void IsDuplicateKey_OtherMongoError_IsFalse() =>
        Assert.False(MongoErrors.IsDuplicateKey(new MongoException("boom")));

    [Fact]
    public void Kind_CommandError_IsTheTypeAndCodeOnly() =>
        Assert.Equal("MongoCommandException:11000", MongoErrors.Kind(CommandError(11000)));

    private static async Task ThrowAfterAwaitAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("holds a document value");
    }

    private static void ThrowNow() => throw new InvalidOperationException("holds a document value");

    private static async Task<Exception> CaughtAsync(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected an exception.");
    }

    [Fact]
    public async Task Origin_ThrownInAnAsyncMethod_IsThatMethodsName()
    {
        var ex = await CaughtAsync(ThrowAfterAwaitAsync);

        Assert.Equal("MongoErrorsTests.ThrowAfterAwaitAsync", MongoErrors.Origin(ex));
    }

    [Fact]
    public async Task Origin_ThrownInASyncMethod_IsThatMethodsName()
    {
        var ex = await CaughtAsync(() =>
        {
            ThrowNow();
            return Task.CompletedTask;
        });

        Assert.Equal("MongoErrorsTests.ThrowNow", MongoErrors.Origin(ex));
    }

    [Fact]
    public async Task Origin_ThrownInALambda_IsTheNamedMethodAroundIt()
    {
        var ex = await CaughtAsync(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("holds a document value");
        });

        Assert.Equal("MongoErrorsTests.CaughtAsync", MongoErrors.Origin(ex));
    }

    [Fact]
    public void Origin_NeverThrown_IsUnknown() =>
        Assert.Equal("unknown", MongoErrors.Origin(new InvalidOperationException("x")));

    [Fact]
    public async Task Origin_AnyException_NeverQuotesTheMessage()
    {
        var ex = await CaughtAsync(ThrowAfterAwaitAsync);

        Assert.DoesNotContain("document value", MongoErrors.Origin(ex));
    }
}
