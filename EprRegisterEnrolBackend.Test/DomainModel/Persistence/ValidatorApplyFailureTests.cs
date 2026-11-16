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
    [Fact]
    public void Classify_Unauthorized_IsNotPermitted() =>
        ValidatorApplyFailure
            .Classify(CommandFailure(13))
            .Should()
            .Be(ValidatorApplyResult.NotPermitted);

    [Fact]
    public void Classify_AtlasRefusesTheAction_IsNotPermitted() =>
        ValidatorApplyFailure
            .Classify(
                CommandFailure(
                    8000,
                    "user is not allowed to do action [collMod] on [epr.applicationVersions]"
                )
            )
            .Should()
            .Be(ValidatorApplyResult.NotPermitted);

    // Atlas uses AtlasError for many other failures too, which may clear on a retry.
    [Fact]
    public void Classify_AnyOtherAtlasError_IsTransient() =>
        ValidatorApplyFailure
            .Classify(CommandFailure(8000, "cluster is being reconfigured"))
            .Should()
            .Be(ValidatorApplyResult.Transient);

    [Theory]
    [InlineData(2)] // BadValue
    [InlineData(91)] // ShutdownInProgress
    [InlineData(189)] // PrimarySteppedDown
    [InlineData(10107)] // NotWritablePrimary
    public void Classify_AnyOtherCommandFailure_IsTransient(int code) =>
        ValidatorApplyFailure
            .Classify(CommandFailure(code))
            .Should()
            .Be(ValidatorApplyResult.Transient);

    [Fact]
    public void Classify_Timeout_IsTransient() =>
        ValidatorApplyFailure
            .Classify(new TimeoutException("server selection timed out"))
            .Should()
            .Be(ValidatorApplyResult.Transient);

    [Fact]
    public void Classify_ConnectionDropped_IsTransient() =>
        ValidatorApplyFailure
            .Classify(new MongoConnectionException(ConnectionId(), "connection reset"))
            .Should()
            .Be(ValidatorApplyResult.Transient);

    private static MongoCommandException CommandFailure(
        int code,
        string message = "collMod failed"
    ) =>
        new(
            ConnectionId(),
            message,
            new BsonDocument("collMod", "applicationVersions"),
            new BsonDocument
            {
                { "ok", 0 },
                { "code", code },
                { "errmsg", message },
            }
        );

    private static ConnectionId ConnectionId() =>
        new(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
}
