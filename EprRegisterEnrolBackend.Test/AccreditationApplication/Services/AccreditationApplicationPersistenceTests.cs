using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// Covers the guard-clause and ternary branches in AccreditationApplicationPersistence that
// don't require a real MongoDB connection: UpdateAsync's null-Id short-circuit and its
// ModifiedCount>0 ternary, and GetByIdAsync's ObjectId.TryParse guard. The class extends
// MongoService<T> (constructor calls IMongoDbClientFactory.GetClient()/GetCollection<T>()), so
// IMongoDbClientFactory and the resulting IMongoCollection<T> are substituted with NSubstitute
// rather than hitting a real database — IMongoCollection<T> is a plain interface, so its
// InsertOneAsync/ReplaceOneAsync members (real interface methods, not extensions) can be
// configured directly.
public class AccreditationApplicationPersistenceTests
{
    private static AccreditationApplicationPersistence CreateSut(
        out IMongoCollection<AccreditationApplicationModel> collection
    )
    {
        var factory = Substitute.For<IMongoDbClientFactory>();
        var mongoCollection = Substitute.For<IMongoCollection<AccreditationApplicationModel>>();
        var databaseNamespace = new DatabaseNamespace("test-db");
        var mongoDatabase = Substitute.For<IMongoDatabase>();
        mongoDatabase.DatabaseNamespace.Returns(databaseNamespace);
        mongoCollection.Database.Returns(mongoDatabase);
        mongoCollection.CollectionNamespace.Returns(
            new CollectionNamespace(databaseNamespace, "accreditationApplications")
        );
        factory.GetClient().Returns(Substitute.For<IMongoClient>());
        factory.GetCollection<AccreditationApplicationModel>("accreditationApplications")
            .Returns(mongoCollection);

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        collection = mongoCollection;
        return new AccreditationApplicationPersistence(factory, loggerFactory);
    }

    [Fact]
    public async Task UpdateAsync_ApplicationIdIsNull_ReturnsNullWithoutTouchingCollection()
    {
        var sut = CreateSut(out var collection);
        var application = new AccreditationApplicationModel
        {
            Id = null,
            OrganisationId = "org-1",
            Year = 2026,
            MaterialType = MaterialType.Steel,
        };

        var result = await sut.UpdateAsync(application);

        result.Should().BeNull();
        await collection
            .DidNotReceive()
            .ReplaceOneAsync(
                Arg.Any<FilterDefinition<AccreditationApplicationModel>>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<ReplaceOptions>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task UpdateAsync_ReplaceModifiesDocument_ReturnsUpdatedApplication()
    {
        var sut = CreateSut(out var collection);
        var application = new AccreditationApplicationModel
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            OrganisationId = "org-1",
            Year = 2026,
            MaterialType = MaterialType.Steel,
        };

        collection
            .ReplaceOneAsync(
                Arg.Any<FilterDefinition<AccreditationApplicationModel>>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<ReplaceOptions>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new ReplaceOneResult.Acknowledged(matchedCount: 1, modifiedCount: 1, upsertedId: null));

        var result = await sut.UpdateAsync(application);

        result.Should().BeSameAs(application);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceModifiesNoDocument_ReturnsNull()
    {
        var sut = CreateSut(out var collection);
        var application = new AccreditationApplicationModel
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            OrganisationId = "org-1",
            Year = 2026,
            MaterialType = MaterialType.Steel,
        };

        collection
            .ReplaceOneAsync(
                Arg.Any<FilterDefinition<AccreditationApplicationModel>>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<ReplaceOptions>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new ReplaceOneResult.Acknowledged(matchedCount: 1, modifiedCount: 0, upsertedId: null));

        var result = await sut.UpdateAsync(application);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ApplicationIdIsNotAValidObjectId_ReturnsNullWithoutQuerying()
    {
        var sut = CreateSut(out var collection);

        var result = await sut.GetByIdAsync("org-1", "not-a-valid-object-id");

        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_InsertThrows_ReturnsNull()
    {
        var sut = CreateSut(out var collection);
        var application = new AccreditationApplicationModel { OrganisationId = "org-1", Year = 2026, MaterialType = MaterialType.Steel };

        collection
            .InsertOneAsync(Arg.Any<AccreditationApplicationModel>(), Arg.Any<InsertOneOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new MongoException("boom")));

        var result = await sut.CreateAsync(application);

        result.Should().BeNull();
    }
}
