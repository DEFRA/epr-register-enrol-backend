using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
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
    // RA-482: the filter-rendering tests below build a serializer for
    // AccreditationApplicationModel, which freezes its class map (and OverseasSiteModel's) at
    // whatever conventions are registered at that moment. Registration is process-global and
    // normally only happens as a side effect of some other test constructing a
    // WebApplicationFactory -- which these substitute-backed tests deliberately don't do -- so
    // without this, running this class first froze OverseasSiteModel with PascalCase element
    // names and broke OverseasSiteBsonDefaultsTests. Same explicit-registration pattern that
    // class already uses, for the same test-ordering reason.
    static AccreditationApplicationPersistenceTests()
    {
        MongoDbClientFactory.EnsureConventionRegistered();
    }

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
        factory
            .GetCollection<AccreditationApplicationModel>("accreditationApplications")
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
            .Returns(
                new ReplaceOneResult.Acknowledged(
                    matchedCount: 1,
                    modifiedCount: 1,
                    upsertedId: null
                )
            );

        var result = await sut.UpdateAsync(application);

        result.Should().BeSameAs(application);
    }

    // RA-516: the filter must additionally require Version to still equal what was read, so a
    // concurrent writer that already moved it on makes this filter match nothing instead of
    // silently overwriting their change.
    [Fact]
    public async Task UpdateAsync_SendsFilterGuardingAgainstAVersionThatHasMovedOn()
    {
        var sut = CreateSut(out var collection);
        var applicationId = MongoDB.Bson.ObjectId.GenerateNewId();
        var application = new AccreditationApplicationModel
        {
            Id = applicationId,
            OrganisationId = "org-1",
            Year = 2026,
            MaterialType = MaterialType.Steel,
            Version = 3,
        };

        FilterDefinition<AccreditationApplicationModel>? captured = null;
        collection
            .ReplaceOneAsync(
                Arg.Do<FilterDefinition<AccreditationApplicationModel>>(f => captured = f),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<ReplaceOptions>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new ReplaceOneResult.Acknowledged(
                    matchedCount: 1,
                    modifiedCount: 1,
                    upsertedId: null
                )
            );

        await sut.UpdateAsync(application);

        captured.Should().NotBeNull();
        var rendered = RenderFilter(captured!);
        rendered.Should().ContainEquivalentOf("version", "the guard is keyed on the Version field");
        rendered
            .Should()
            .Contain("3", "the guard must require the version last read, not the new one");
    }

    [Fact]
    public async Task UpdateAsync_ReplaceModifiesDocument_IncrementsVersion()
    {
        var sut = CreateSut(out var collection);
        var application = new AccreditationApplicationModel
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            OrganisationId = "org-1",
            Year = 2026,
            MaterialType = MaterialType.Steel,
            Version = 5,
        };

        collection
            .ReplaceOneAsync(
                Arg.Any<FilterDefinition<AccreditationApplicationModel>>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<ReplaceOptions>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new ReplaceOneResult.Acknowledged(
                    matchedCount: 1,
                    modifiedCount: 1,
                    upsertedId: null
                )
            );

        var result = await sut.UpdateAsync(application);

        result.Should().NotBeNull();
        result!.Version.Should().Be(6);
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
            .Returns(
                new ReplaceOneResult.Acknowledged(
                    matchedCount: 1,
                    modifiedCount: 0,
                    upsertedId: null
                )
            );

        var result = await sut.UpdateAsync(application);

        result.Should().BeNull();
    }

    // RA-482: same guard-clause and ternary shape as UpdateAsync above, plus the null-Id
    // short-circuit must apply here too since it's a second write path into the same collection.
    [Fact]
    public async Task UpdateIfOrsIdAbsentAsync_ApplicationIdIsNull_ReturnsNullWithoutTouchingCollection()
    {
        var sut = CreateSut(out var collection);
        var application = new AccreditationApplicationModel
        {
            Id = null,
            OrganisationId = "org-1",
            Year = 2026,
            MaterialType = MaterialType.Steel,
        };

        var result = await sut.UpdateIfOrsIdAbsentAsync(application, "001");

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
    public async Task UpdateIfOrsIdAbsentAsync_ReplaceModifiesDocument_ReturnsUpdatedApplication()
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
            .Returns(
                new ReplaceOneResult.Acknowledged(
                    matchedCount: 1,
                    modifiedCount: 1,
                    upsertedId: null
                )
            );

        var result = await sut.UpdateIfOrsIdAbsentAsync(application, "001");

        result.Should().BeSameAs(application);
    }

    // A concurrent writer already claiming the id is indistinguishable, at this layer, from any
    // other reason the guarded filter matched nothing -- modifiedCount 0 always means "did not
    // persist," which is exactly what the caller's retry loop needs to know.
    [Fact]
    public async Task UpdateIfOrsIdAbsentAsync_ReplaceModifiesNoDocument_ReturnsNull()
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
            .Returns(
                new ReplaceOneResult.Acknowledged(
                    matchedCount: 1,
                    modifiedCount: 0,
                    upsertedId: null
                )
            );

        var result = await sut.UpdateIfOrsIdAbsentAsync(application, "001");

        result.Should().BeNull();
    }

    // RA-482: the OrsId-absence clause is the whole reason UpdateIfOrsIdAbsentAsync exists as a
    // second write path -- it and UpdateAsync differ only in their filter, sharing
    // ReplaceIfMatchAsync for everything else. Drop the Not(ElemMatch(...)) clause and the method
    // silently degrades into UpdateAsync, reintroducing the exact race this feature closes. Every
    // other test in this class mocks ReplaceOneAsync with Arg.Any<FilterDefinition<...>> and drives
    // the outcome purely from the canned ReplaceOneResult, so the guard itself goes unasserted.
    // These two tests capture and render the real filter instead, so removing the clause fails the
    // suite rather than passing it silently.
    private static string RenderFilter(FilterDefinition<AccreditationApplicationModel> filter)
    {
        var registry = BsonSerializer.SerializerRegistry;
        return filter
            .Render(
                new RenderArgs<AccreditationApplicationModel>(
                    registry.GetSerializer<AccreditationApplicationModel>(),
                    registry
                )
            )
            .ToJson();
    }

    // Element names are asserted case-insensitively: whether the camelCase convention is active
    // depends on MongoDbClientFactory having been constructed, which these substitute-backed tests
    // deliberately avoid, and the guard's presence -- not its casing -- is what's under test.
    [Fact]
    public async Task UpdateIfOrsIdAbsentAsync_SendsFilterGuardingAgainstThatOrsIdAlreadyBeingPresent()
    {
        var sut = CreateSut(out var collection);
        var applicationId = MongoDB.Bson.ObjectId.GenerateNewId();
        var application = new AccreditationApplicationModel
        {
            Id = applicationId,
            OrganisationId = "org-1",
            Year = 2026,
            MaterialType = MaterialType.Steel,
        };

        FilterDefinition<AccreditationApplicationModel>? captured = null;
        collection
            .ReplaceOneAsync(
                Arg.Do<FilterDefinition<AccreditationApplicationModel>>(f => captured = f),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<ReplaceOptions>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new ReplaceOneResult.Acknowledged(
                    matchedCount: 1,
                    modifiedCount: 1,
                    upsertedId: null
                )
            );

        await sut.UpdateIfOrsIdAbsentAsync(application, "007");

        captured.Should().NotBeNull();
        var rendered = RenderFilter(captured!);
        rendered
            .Should()
            .Contain(applicationId.ToString(), "the write must still target this document");
        rendered
            .Should()
            .Contain("$elemMatch", "the guard inspects the OverseasSites.Sites array element-wise");
        rendered
            .Should()
            .ContainEquivalentOf("orsId", "the guard is keyed on the site's OrsId field");
        rendered.Should().Contain("007", "the guard must be scoped to the id being claimed");
        rendered
            .Should()
            .ContainEquivalentOf("version", "shares ReplaceIfMatchAsync's version guard too");
    }

    [Fact]
    public async Task UpdateAsync_SendsPlainIdFilterWithNoOrsIdGuard()
    {
        var sut = CreateSut(out var collection);
        var applicationId = MongoDB.Bson.ObjectId.GenerateNewId();
        var application = new AccreditationApplicationModel
        {
            Id = applicationId,
            OrganisationId = "org-1",
            Year = 2026,
            MaterialType = MaterialType.Steel,
        };

        FilterDefinition<AccreditationApplicationModel>? captured = null;
        collection
            .ReplaceOneAsync(
                Arg.Do<FilterDefinition<AccreditationApplicationModel>>(f => captured = f),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<ReplaceOptions>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new ReplaceOneResult.Acknowledged(
                    matchedCount: 1,
                    modifiedCount: 1,
                    upsertedId: null
                )
            );

        await sut.UpdateAsync(application);

        captured.Should().NotBeNull();
        var rendered = RenderFilter(captured!);
        rendered.Should().Contain(applicationId.ToString());
        rendered
            .Should()
            .NotContain(
                "$elemMatch",
                "the unguarded update path must stay unguarded -- if this ever gains the OrsId "
                    + "clause the two paths have been conflated in the wrong direction"
            );
        rendered.Should().NotContainEquivalentOf("orsId");
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
        var application = new AccreditationApplicationModel
        {
            OrganisationId = "org-1",
            Year = 2026,
            MaterialType = MaterialType.Steel,
        };

        collection
            .InsertOneAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<InsertOneOptions>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromException(new MongoException("boom")));

        var result = await sut.CreateAsync(application);

        result.Should().BeNull();
    }
}
