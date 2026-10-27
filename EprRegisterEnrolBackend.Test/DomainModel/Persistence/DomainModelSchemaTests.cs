using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.DomainModel.Persistence;

/// <summary>Phase 1 "Expand": the empty schema exists after the persistence classes are constructed, and is verifiable.</summary>
public sealed class DomainModelSchemaTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly TestClock _clock = new(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));

    public DomainModelSchemaTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("domainschema");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private List<IDomainModelCollection> AllCollections() =>
        [
            new OrganisationPersistence(_factory, NullLoggerFactory.Instance),
            new RegistrationPersistence(_factory, NullLoggerFactory.Instance, _clock),
            new OverseasSitePersistence(_factory, NullLoggerFactory.Instance, _clock),
            new ApplicationVersionPersistence(_factory, NullLoggerFactory.Instance, _clock),
            new AccreditationPersistence(_factory, NullLoggerFactory.Instance, _clock),
        ];

    [Fact]
    public async Task Constructing_the_persistence_classes_creates_every_collection_with_all_its_indexes()
    {
        var collections = AllCollections();

        collections
            .Select(c => c.CollectionName)
            .Should()
            .BeEquivalentTo(
                "organisations",
                "registrationVersions",
                "overseasSiteVersions",
                "applicationVersions",
                "accreditationVersions"
            );
        foreach (var collection in collections)
        {
            collection.ExpectedIndexNames.Should().NotBeEmpty();
            var actual = await collection.GetIndexNamesAsync(Ct);
            actual
                .Should()
                .BeEquivalentTo(collection.ExpectedIndexNames, collection.CollectionName);
        }
    }

    [Fact]
    public async Task Every_versioned_collection_has_the_identity_indexes()
    {
        foreach (
            var collection in AllCollections()
                .Where(c => c.CollectionName.EndsWith("Versions", StringComparison.Ordinal))
        )
        {
            (await collection.GetIndexNamesAsync(Ct))
                .Should()
                .Contain(
                    ["entityId_version_u", "entityId_current_u", "entityId_asof"],
                    collection.CollectionName
                );
        }
    }

    [Fact]
    public async Task Constructing_them_again_is_idempotent()
    {
        AllCollections();

        var second = AllCollections();

        foreach (var collection in second)
        {
            (await collection.GetIndexNamesAsync(Ct))
                .Should()
                .BeEquivalentTo(collection.ExpectedIndexNames);
        }
    }

    [Fact]
    public async Task A_conflicting_existing_index_is_reconciled_not_fatal()
    {
        // An older deploy created the same-named index without the partial filter.
        var registrations = _factory.GetCollection<RegistrationEntity>("registrationVersions");
        await registrations.Indexes.CreateOneAsync(
            new CreateIndexModel<RegistrationEntity>(
                Builders<RegistrationEntity>.IndexKeys.Ascending("registrationNumber"),
                new CreateIndexOptions { Name = "registrationNumber_current_u", Unique = true }
            ),
            cancellationToken: Ct
        );

        var sut = new RegistrationPersistence(_factory, NullLoggerFactory.Instance, _clock);

        (await sut.GetIndexNamesAsync(Ct)).Should().Contain("registrationNumber_current_u");
        var index = (await (await registrations.Indexes.ListAsync(Ct)).ToListAsync(Ct)).Single(i =>
            i["name"] == "registrationNumber_current_u"
        );
        index.Contains("partialFilterExpression").Should().BeTrue();
    }

    [Fact]
    public async Task The_health_check_is_healthy_once_the_schema_exists()
    {
        var provider = Provider(AllCollections());

        var result = await new DomainModelSchemaHealthCheck(provider).CheckHealthAsync(
            new HealthCheckContext(),
            Ct
        );

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task The_health_check_names_the_collection_and_index_that_is_missing()
    {
        var broken = Substitute.For<IDomainModelCollection>();
        broken.CollectionName.Returns("registrationVersions");
        broken.ExpectedIndexNames.Returns(["entityId_current_u", "registrationNumber_current_u"]);
        broken
            .GetIndexNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["entityId_current_u"]));

        var result = await new DomainModelSchemaHealthCheck(Provider([broken])).CheckHealthAsync(
            new HealthCheckContext(),
            Ct
        );

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result
            .Description.Should()
            .Contain("registrationVersions")
            .And.Contain("registrationNumber_current_u");
        result.Description.Should().NotContain("entityId_current_u,");
    }

    [Fact]
    public async Task The_health_check_is_unhealthy_when_a_collection_does_not_exist_yet()
    {
        var absent = Substitute.For<IDomainModelCollection>();
        absent.CollectionName.Returns("accreditationVersions");
        absent.ExpectedIndexNames.Returns(["entityId_version_u"]);
        absent
            .GetIndexNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));

        var result = await new DomainModelSchemaHealthCheck(Provider([absent])).CheckHealthAsync(
            new HealthCheckContext(),
            Ct
        );

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task The_health_check_reports_unhealthy_with_a_short_message_when_mongo_cannot_be_reached()
    {
        var failing = Substitute.For<IDomainModelCollection>();
        failing
            .GetIndexNamesAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<string>>>(_ =>
                throw new TimeoutException(
                    "server selection timed out for mongodb://user:secret@host"
                )
            );

        var result = await new DomainModelSchemaHealthCheck(Provider([failing])).CheckHealthAsync(
            new HealthCheckContext(),
            Ct
        );

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("TimeoutException").And.NotContain("secret");
    }

    private static IServiceProvider Provider(IEnumerable<IDomainModelCollection> collections)
    {
        var services = new ServiceCollection();
        foreach (var collection in collections)
        {
            services.AddSingleton(collection);
        }

        return services.BuildServiceProvider();
    }
}
