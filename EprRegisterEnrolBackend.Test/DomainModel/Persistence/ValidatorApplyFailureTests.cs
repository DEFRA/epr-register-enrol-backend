using System.Net;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;

namespace EprRegisterEnrolBackend.Test.DomainModel.Persistence;

/// <summary>
/// Which collMod failures are worth retrying (RA-758). A database user that may not run collMod stays that way, so
/// retrying it is pointless; anything else, such as a dropped connection or a failover, may clear within seconds. The
/// test Mongo runs without authentication, so the permission codes are checked here rather than against a server.
/// </summary>
public sealed class ValidatorApplyFailureTests
{
    [Theory]
    [InlineData(13)] // Unauthorized
    [InlineData(8000)] // AtlasError, which Atlas returns for an action the user's role does not allow
    public void Classify_PermissionDenied_IsNotPermitted(int code) =>
        ValidatorApplyFailure
            .Classify(CommandFailure(code))
            .Should()
            .Be(ValidatorApplyResult.NotPermitted);

    [Theory]
    [InlineData(2)] // BadValue
    [InlineData(91)] // ShutdownInProgress
    [InlineData(189)] // PrimarySteppedDown
    [InlineData(10107)] // NotWritablePrimary
    public void Classify_AnyOtherCommandFailure_IsFailed(int code) =>
        ValidatorApplyFailure
            .Classify(CommandFailure(code))
            .Should()
            .Be(ValidatorApplyResult.Failed);

    [Fact]
    public void Classify_Timeout_IsFailed() =>
        ValidatorApplyFailure
            .Classify(new TimeoutException("server selection timed out"))
            .Should()
            .Be(ValidatorApplyResult.Failed);

    [Fact]
    public void Classify_ConnectionDropped_IsFailed() =>
        ValidatorApplyFailure
            .Classify(new MongoConnectionException(ConnectionId(), "connection reset"))
            .Should()
            .Be(ValidatorApplyResult.Failed);

    private static MongoCommandException CommandFailure(int code) =>
        new(
            ConnectionId(),
            "collMod failed",
            new BsonDocument("collMod", "applicationVersions"),
            new BsonDocument
            {
                { "ok", 0 },
                { "code", code },
                { "errmsg", "collMod failed" },
            }
        );

    private static ConnectionId ConnectionId() =>
        new(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
}
