using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Persistence;

/// <summary>
/// The <c>$jsonSchema</c> validator on <c>applicationVersions</c> (RA-746): a cheap backstop against any writer saving
/// an unknown type or another type's fields. It runs in <c>warn</c> mode, so a bad rule only logs instead of failing a
/// dual-write request, and <c>moderate</c> level, so closing an old version never re-checks it.
/// </summary>
public sealed class ApplicationValidatorTests : IDisposable
{
    private const int DocumentValidationFailure = 121;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly TestClock _clock = new(new DateTime(2027, 3, 1, 9, 0, 0, DateTimeKind.Utc));
    private readonly IMongoDatabase _database;

    public ApplicationValidatorTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("validator");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _database = _factory.GetClient().GetDatabase(_databaseName);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private ApplicationVersionPersistence Persistence() =>
        new(_factory, NullLoggerFactory.Instance, _clock);

    private static ChangeInfo Change() => new() { Reason = ChangeReason.ApplicationEdited };

    private static BsonDocument Validator() => ApplicationVersionSchema.Validator();

    private async Task<BsonDocument> CollectionOptionsAsync(string name)
    {
        using var cursor = await _database.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = new BsonDocument("name", name) },
            Ct
        );
        return (await cursor.SingleAsync(Ct))["options"].AsBsonDocument;
    }

    private Task<long> CountFailingAsync(string collection) =>
        _database
            .GetCollection<BsonDocument>(collection)
            .CountDocumentsAsync(
                new BsonDocument("$nor", new BsonArray { Validator() }),
                cancellationToken: Ct
            );

    private static IEnumerable<ApplicationEntity> OneOfEachType() =>
        [
            DomainModelBuilders.Application(
                ObjectId.GenerateNewId(),
                DomainModelBuilders.AccreditationDetails()
            ),
            DomainModelBuilders.Application(
                ObjectId.GenerateNewId(),
                DomainModelBuilders.OrsChangeDetails(ObjectId.GenerateNewId())
            ),
            DomainModelBuilders.Application(
                ObjectId.GenerateNewId(),
                DomainModelBuilders.TonnageChangeDetails()
            ),
            DomainModelBuilders.Application(
                ObjectId.GenerateNewId(),
                DomainModelBuilders.NewRegistrationDetails()
            ),
            DomainModelBuilders.Application(
                ObjectId.GenerateNewId(),
                DomainModelBuilders.AnnualChargeDetails()
            ),
        ];

    // --- applied at startup ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Startup_NewCollection_HasTheValidatorInWarnAndModerateMode()
    {
        _ = Persistence();

        var options = await CollectionOptionsAsync(DomainModelCollections.ApplicationVersions);

        options["validator"].Should().Be(Validator());
        options["validationLevel"].AsString.Should().Be("moderate");
        options["validationAction"].AsString.Should().Be("warn");
    }

    [Fact]
    public async Task Startup_ExistingCollectionWithDocuments_GetsTheValidator()
    {
        var existing = _database.GetCollection<BsonDocument>(
            DomainModelCollections.ApplicationVersions
        );
        await existing.InsertOneAsync(
            new BsonDocument("legacyShaped", true),
            cancellationToken: Ct
        );

        _ = Persistence();

        var options = await CollectionOptionsAsync(DomainModelCollections.ApplicationVersions);
        options["validator"].Should().Be(Validator());
        (
            await existing.CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty,
                cancellationToken: Ct
            )
        )
            .Should()
            .Be(1, "applying a validator never touches the documents already there");
    }

    // With no indexes there is nothing to create the collection first, so collMod finds none (NamespaceNotFound) and
    // the collection is created with the validator, in the same warn and moderate mode as collMod would set.
    [Fact]
    public async Task Startup_CollectionNotYetCreated_IsCreatedWithTheSameValidatorAndMode()
    {
        _ = new ValidatorOnlyPersistence(_factory);

        var options = await CollectionOptionsAsync(ValidatorOnlyPersistence.Name);

        options["validator"].Should().Be(ValidatorOnlyPersistence.Schema());
        options["validationLevel"].AsString.Should().Be("moderate");
        options["validationAction"].AsString.Should().Be("warn");
    }

    [Fact]
    public async Task Startup_ValidatorCannotBeApplied_IsNotFatal_AndTheCheckReportsDegraded()
    {
        var persistence = new UnappliableValidatorPersistence(_factory, _clock);

        var result = await DomainModelSchemaCheck.RunAsync([persistence], Ct);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain(DomainModelCollections.ApplicationVersions);
    }

    // --- what it flags --------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryType_AsTheBuildersWriteIt_Passes()
    {
        var persistence = Persistence();
        foreach (var application in OneOfEachType())
        {
            await persistence.Versions.InsertAsync(application, Change(), cancellationToken: Ct);
        }

        (await CountFailingAsync(DomainModelCollections.ApplicationVersions)).Should().Be(0);
    }

    [Fact]
    public async Task AnotherTypesDetails_UnknownTypeOrMissingEnvelopeField_IsFlagged()
    {
        var persistence = Persistence();
        var good = await persistence.Versions.InsertAsync(
            DomainModelBuilders.Application(
                ObjectId.GenerateNewId(),
                DomainModelBuilders.AccreditationDetails()
            ),
            Change(),
            cancellationToken: Ct
        );
        var raw = _database.GetCollection<BsonDocument>(DomainModelCollections.ApplicationVersions);
        var stored = await raw.Find(new BsonDocument("_id", good.Id)).SingleAsync(Ct);

        BsonDocument Variant(Action<BsonDocument> change)
        {
            var copy = stored.DeepClone().AsBsonDocument;
            copy["_id"] = ObjectId.GenerateNewId();
            copy["entityId"] = ObjectId.GenerateNewId();
            copy.Remove("applicationReference"); // unique among current versions
            change(copy);
            return copy;
        }

        // Warn mode never refuses a write, so these all land; the validator still names them as failing.
        await raw.InsertManyAsync(
            [
                Variant(d => d["applicationType"] = "OrsChange"), // accreditation details under another type
                Variant(d => d["applicationType"] = "SomethingNew"), // unknown type
                Variant(d => d.Remove("status")), // envelope field missing
                Variant(d => d["validFrom"] = "2027-03-01"), // envelope field of the wrong type
                Variant(d => d["details"].AsBsonDocument.Remove("schemaVersion")),
            ],
            cancellationToken: Ct
        );

        (await CountFailingAsync(DomainModelCollections.ApplicationVersions)).Should().Be(5);
    }

    // IgnoreIfNull leaves a null field out of the stored document, so a FromBand that is null is simply absent, and
    // the validator allows that. ToBand is required by both the type (non-nullable) and the validator; a document
    // written without one by anything other than the C# code is flagged.
    [Fact]
    public async Task TonnageChange_WithNoFromBand_Passes_ButWithNoToBandIsFlagged()
    {
        var persistence = Persistence();
        var noFrom = DomainModelBuilders.TonnageChangeDetails();
        noFrom.FromBand = null;
        var stored = await persistence.Versions.InsertAsync(
            DomainModelBuilders.Application(ObjectId.GenerateNewId(), noFrom),
            Change(),
            cancellationToken: Ct
        );
        (await CountFailingAsync(DomainModelCollections.ApplicationVersions)).Should().Be(0);

        var raw = _database.GetCollection<BsonDocument>(DomainModelCollections.ApplicationVersions);
        var noTo = await raw.Find(new BsonDocument("_id", stored.Id)).SingleAsync(Ct);
        noTo["_id"] = ObjectId.GenerateNewId();
        noTo["entityId"] = ObjectId.GenerateNewId();
        noTo.Remove("applicationReference"); // unique among current versions
        noTo["details"].AsBsonDocument.Remove("toBand");
        await raw.InsertOneAsync(noTo, cancellationToken: Ct);

        (await CountFailingAsync(DomainModelCollections.ApplicationVersions)).Should().Be(1);
    }

    [Fact]
    public async Task InErrorMode_AnotherTypesDetails_IsRejected()
    {
        // A test-only copy in error mode: the real collection stays in warn until the logs are clean.
        await _database.CreateCollectionAsync(
            "applicationVersionsStrict",
            new CreateCollectionOptions<BsonDocument>
            {
                Validator = Validator(),
                ValidationLevel = DocumentValidationLevel.Moderate,
                ValidationAction = DocumentValidationAction.Error,
            },
            Ct
        );
        var strict = _database.GetCollection<ApplicationEntity>("applicationVersionsStrict");
        var good = DomainModelBuilders.Application(
            ObjectId.GenerateNewId(),
            DomainModelBuilders.TonnageChangeDetails()
        );
        await strict.InsertOneAsync(good, cancellationToken: Ct);
        var mismatched = strict.Database.GetCollection<BsonDocument>("applicationVersionsStrict");
        var bad = good.ToBsonDocument();
        bad["_id"] = ObjectId.GenerateNewId();
        bad["applicationType"] = "AnnualCharge";

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() =>
            mismatched.InsertOneAsync(bad, cancellationToken: Ct)
        );
        ex.WriteError.Code.Should().Be(DocumentValidationFailure);
    }

    // --- versioning under a changed schema ------------------------------------------------------------------------

    // Closing a version is a $set on the old document. If the schema has tightened since that document was written,
    // strict would re-check it and refuse the close, failing every later write to that application; moderate doesn't.
    // Error mode is used here so a refused write would throw rather than only log.
    [Theory]
    [InlineData("moderate", true)]
    [InlineData("strict", false)]
    public async Task ClosingAnOldVersion_AfterTheSchemaTightens_WorksOnlyUnderModerate(
        string level,
        bool closes
    )
    {
        var persistence = Persistence();
        var v1 = DomainModelBuilders.Application(
            ObjectId.GenerateNewId(),
            DomainModelBuilders.AnnualChargeDetails()
        );
        v1.ApplicationReference = null;
        var created = await persistence.Versions.InsertAsync(v1, Change(), cancellationToken: Ct);

        // The schema tightens: applicationReference is now required, which version 1 doesn't have.
        var tightened = Validator();
        tightened["$jsonSchema"]["required"].AsBsonArray.Add("applicationReference");
        await _database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                { "collMod", DomainModelCollections.ApplicationVersions },
                { "validator", tightened },
                { "validationLevel", level },
                { "validationAction", "error" },
            },
            cancellationToken: Ct
        );

        created.ApplicationReference = "APP-NOW-SET";
        var update = () =>
            persistence.Versions.UpdateAsync(
                created,
                created.Version,
                Change(),
                cancellationToken: Ct
            );

        if (closes)
        {
            (await update()).Entity.Version.Should().Be(2);
        }
        else
        {
            await update.Should().ThrowAsync<MongoException>();
        }
    }

    // --- the schema check -----------------------------------------------------------------------------------------

    [Fact]
    public async Task SchemaCheck_ValidatorPresent_IsHealthy()
    {
        var result = await DomainModelSchemaCheck.RunAsync([Persistence()], Ct);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task SchemaCheck_ValidatorRemoved_IsDegradedNotUnhealthy()
    {
        var persistence = Persistence();
        await _database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                { "collMod", DomainModelCollections.ApplicationVersions },
                { "validator", new BsonDocument() },
            },
            cancellationToken: Ct
        );

        var result = await DomainModelSchemaCheck.RunAsync([persistence], Ct);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain(DomainModelCollections.ApplicationVersions);
    }

    // --- applying it again (RA-758) -------------------------------------------------------------------------------

    [Fact]
    public async Task TryApplyValidator_AfterTheValidatorWasLost_PutsItBack()
    {
        var persistence = Persistence();
        await _database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                { "collMod", DomainModelCollections.ApplicationVersions },
                { "validator", new BsonDocument() },
            },
            cancellationToken: Ct
        );

        var result = persistence.TryApplyValidator();

        result.Should().Be(ValidatorApplyResult.Applied);
        persistence.LastValidatorApplyResult.Should().Be(ValidatorApplyResult.Applied);
        (await DomainModelSchemaCheck.RunAsync([persistence], Ct))
            .Status.Should()
            .Be(HealthStatus.Healthy);
    }

    [Fact]
    public void TryApplyValidator_MongoRefusesTheCommand_ReportsTransientWithoutThrowing()
    {
        var persistence = new UnappliableValidatorPersistence(_factory, _clock);

        var result = persistence.TryApplyValidator();

        result.Should().Be(ValidatorApplyResult.Transient);
        persistence.LastValidatorApplyResult.Should().Be(ValidatorApplyResult.Transient);
    }

    // TryApplyValidator never throws, even for a failure that is not Mongo's: it first runs in the constructor, where an
    // escaping exception would stop the service starting.
    [Fact]
    public void TryApplyValidator_NonMongoFailure_ReportsTransientWithoutThrowing()
    {
        var construct = () => new ThrowingValidatorPersistence(_factory, _clock);

        var persistence = construct.Should().NotThrow().Which;

        persistence.LastValidatorApplyResult.Should().Be(ValidatorApplyResult.Transient);
        persistence.TryApplyValidator().Should().Be(ValidatorApplyResult.Transient);
    }

    [Fact]
    public void TryApplyValidator_NoValidatorDefined_HasNothingToApply()
    {
        var persistence = new NoValidatorPersistence(_factory);

        persistence.TryApplyValidator().Should().Be(ValidatorApplyResult.NoValidator);
    }

    [Fact]
    public async Task SchemaCheck_ValidatorInAnotherMode_IsDegraded()
    {
        var persistence = Persistence();
        await _database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                { "collMod", DomainModelCollections.ApplicationVersions },
                { "validationAction", "error" },
            },
            cancellationToken: Ct
        );

        var result = await DomainModelSchemaCheck.RunAsync([persistence], Ct);

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    // A validator Mongo refuses ($where is not allowed in one), standing in for a database user that may not run
    // collMod: applying it fails with a MongoCommandException, as a permission failure (code 13) does, and that must
    // not stop the service starting. The real permission case needs a server with authentication on, which the test
    // Mongo doesn't run.
    private sealed class UnappliableValidatorPersistence(
        IMongoDbClientFactory factory,
        TimeProvider clock
    ) : ApplicationVersionPersistence(factory, NullLoggerFactory.Instance, clock)
    {
        protected override BsonDocument? DefineValidator() =>
            new("$where", "function() { return true; }");
    }

    // A validator that fails to build with something other than a Mongo error, which must still not escape.
    private sealed class ThrowingValidatorPersistence(
        IMongoDbClientFactory factory,
        TimeProvider clock
    ) : ApplicationVersionPersistence(factory, NullLoggerFactory.Instance, clock)
    {
        protected override BsonDocument? DefineValidator() =>
            throw new InvalidOperationException("validator could not be built");
    }

    // A collection with no validator at all, the default for a domain model collection.
    private sealed class NoValidatorPersistence(IMongoDbClientFactory factory)
        : DomainModelPersistence<BsonDocument>(factory, "noValidator", NullLoggerFactory.Instance)
    {
        protected override List<CreateIndexModel<BsonDocument>> DefineIndexes(
            IndexKeysDefinitionBuilder<BsonDocument> builder
        ) => [];
    }

    // A collection with a validator and no indexes, so nothing creates it before the validator is applied.
    private sealed class ValidatorOnlyPersistence(IMongoDbClientFactory factory)
        : DomainModelPersistence<BsonDocument>(factory, Name, NullLoggerFactory.Instance)
    {
        public const string Name = "validatorOnly";

        public static BsonDocument Schema() =>
            new("$jsonSchema", new BsonDocument("required", new BsonArray { "name" }));

        protected override List<CreateIndexModel<BsonDocument>> DefineIndexes(
            IndexKeysDefinitionBuilder<BsonDocument> builder
        ) => [];

        protected override BsonDocument? DefineValidator() => Schema();
    }
}
