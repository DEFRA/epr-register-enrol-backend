using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// Covers RegulatoryNumberSequenceCounterPersistence's real Mongo-backed IncrementAsync and
// GetCurrentMaxAsync - everywhere else in this test suite exercises
// FakeRegulatoryNumberSequenceCounterPersistence instead, so this class's own methods were
// otherwise never actually run. Mirrors AccreditationApplicationPersistenceTests: extends
// MongoService<T> (constructor calls IMongoDbClientFactory.GetClient()/GetCollection<T>()), so
// IMongoDbClientFactory and the resulting IMongoCollection<T> are substituted with NSubstitute
// rather than hitting a real database.
public class RegulatoryNumberSequenceCounterPersistenceTests
{
    private static RegulatoryNumberSequenceCounterPersistence CreateSut(
        out IMongoCollection<RegulatoryNumberSequenceCounter> collection
    )
    {
        var factory = Substitute.For<IMongoDbClientFactory>();
        var mongoCollection = Substitute.For<IMongoCollection<RegulatoryNumberSequenceCounter>>();
        var databaseNamespace = new DatabaseNamespace("test-db");
        var mongoDatabase = Substitute.For<IMongoDatabase>();
        mongoDatabase.DatabaseNamespace.Returns(databaseNamespace);
        mongoCollection.Database.Returns(mongoDatabase);
        mongoCollection.CollectionNamespace.Returns(
            new CollectionNamespace(databaseNamespace, "regulatoryNumberSequences")
        );
        factory.GetClient().Returns(Substitute.For<IMongoClient>());
        factory
            .GetCollection<RegulatoryNumberSequenceCounter>("regulatoryNumberSequences")
            .Returns(mongoCollection);

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        collection = mongoCollection;
        return new RegulatoryNumberSequenceCounterPersistence(factory, loggerFactory);
    }

    [Fact]
    public async Task IncrementAsync_ReturnsTheUpdatedCurrentMax()
    {
        var sut = CreateSut(out var collection);
        collection
            .FindOneAndUpdateAsync(
                Arg.Any<FilterDefinition<RegulatoryNumberSequenceCounter>>(),
                Arg.Any<UpdateDefinition<RegulatoryNumberSequenceCounter>>(),
                Arg.Any<FindOneAndUpdateOptions<RegulatoryNumberSequenceCounter>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new RegulatoryNumberSequenceCounter { Id = "R-ER", CurrentMax = 7 });

        var result = await sut.IncrementAsync("R-ER", TestContext.Current.CancellationToken);

        result.Should().Be(7);
    }

    [Fact]
    public async Task IncrementAsync_PassesAnUpsertingAtomicIncrementToTheCollection()
    {
        var sut = CreateSut(out var collection);
        collection
            .FindOneAndUpdateAsync(
                Arg.Any<FilterDefinition<RegulatoryNumberSequenceCounter>>(),
                Arg.Any<UpdateDefinition<RegulatoryNumberSequenceCounter>>(),
                Arg.Any<FindOneAndUpdateOptions<RegulatoryNumberSequenceCounter>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new RegulatoryNumberSequenceCounter { Id = "R-ER", CurrentMax = 1 });

        await sut.IncrementAsync("R-ER", TestContext.Current.CancellationToken);

        await collection
            .Received(1)
            .FindOneAndUpdateAsync(
                Arg.Any<FilterDefinition<RegulatoryNumberSequenceCounter>>(),
                Arg.Any<UpdateDefinition<RegulatoryNumberSequenceCounter>>(),
                Arg.Is<FindOneAndUpdateOptions<RegulatoryNumberSequenceCounter>>(o =>
                    o.IsUpsert && o.ReturnDocument == ReturnDocument.After
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task GetCurrentMaxAsync_KeyExists_ReturnsItsCurrentMax()
    {
        var sut = CreateSut(out var collection);
        var cursor = Substitute.For<IAsyncCursor<RegulatoryNumberSequenceCounter>>();
        cursor.Current.Returns([
            new RegulatoryNumberSequenceCounter { Id = "R-ER", CurrentMax = 42 },
        ]);
        cursor
            .MoveNextAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true), Task.FromResult(false));
        // Find(...).FirstOrDefaultAsync() is a fluent wrapper around the real
        // IMongoCollection<T> member FindAsync - that's the call actually reaching the
        // driver, so that's what needs substituting, not the (non-existent) IFindFluent
        // extension surface.
        collection
            .FindAsync(
                Arg.Any<FilterDefinition<RegulatoryNumberSequenceCounter>>(),
                Arg.Any<
                    FindOptions<RegulatoryNumberSequenceCounter, RegulatoryNumberSequenceCounter>
                >(),
                Arg.Any<CancellationToken>()
            )
            .Returns(cursor);

        var result = await sut.GetCurrentMaxAsync("R-ER", TestContext.Current.CancellationToken);

        result.Should().Be(42);
    }

    [Fact]
    public async Task GetCurrentMaxAsync_KeyDoesNotExist_ReturnsNull()
    {
        var sut = CreateSut(out var collection);
        var cursor = Substitute.For<IAsyncCursor<RegulatoryNumberSequenceCounter>>();
        cursor.Current.Returns([]);
        cursor.MoveNextAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        collection
            .FindAsync(
                Arg.Any<FilterDefinition<RegulatoryNumberSequenceCounter>>(),
                Arg.Any<
                    FindOptions<RegulatoryNumberSequenceCounter, RegulatoryNumberSequenceCounter>
                >(),
                Arg.Any<CancellationToken>()
            )
            .Returns(cursor);

        var result = await sut.GetCurrentMaxAsync(
            "R-MISSING",
            TestContext.Current.CancellationToken
        );

        result.Should().BeNull();
    }
}
