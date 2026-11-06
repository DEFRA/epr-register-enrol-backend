using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>
/// Creates a domain model collection's indexes when it is constructed (the base <see cref="MongoService{T}"/>
/// behaviour, which is why these classes derive from it), applies its <c>$jsonSchema</c> validator if it has one, and
/// reports both for the schema check. Registering the persistence classes is therefore what "expands" the schema in
/// each environment.
/// </summary>
public abstract class DomainModelPersistence<T> : MongoService<T>, IDomainModelCollection
{
    private const int NamespaceNotFound = 26;
    private const int NamespaceExists = 48;

    /// <summary>
    /// <c>moderate</c>, so closing an old version (a <c>$set</c> on a document written under an older schema) is never
    /// re-checked; <c>warn</c>, so a bad rule logs instead of failing a dual-write request (RA-746).
    /// </summary>
    public const string ValidationLevel = "moderate";

    public const string ValidationAction = "warn";

    protected DomainModelPersistence(
        IMongoDbClientFactory connectionFactory,
        string collectionName,
        ILoggerFactory loggerFactory
    )
        : base(connectionFactory, collectionName, loggerFactory)
    {
        EnsureValidator();
    }

    public string CollectionName => Collection.CollectionNamespace.CollectionName;

    public IReadOnlyList<string> GetExpectedIndexNames() =>
        DefineIndexes(Builders<T>.IndexKeys).Select(m => m.Options.Name).ToList();

    public async Task<IReadOnlyList<string>> GetIndexNamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cursor = await Collection.Indexes.ListAsync(cancellationToken);
            var documents = await cursor.ToListAsync(cancellationToken);
            return documents.Select(d => d["name"].AsString).Where(n => n != "_id_").ToList();
        }
        catch (MongoCommandException ex) when (ex.Code == NamespaceNotFound)
        {
            return [];
        }
    }

    public BsonDocument? GetExpectedValidation() =>
        DefineValidator() is { } validator ? Validation(validator) : null;

    public async Task<BsonDocument?> GetValidationAsync(CancellationToken cancellationToken)
    {
        using var cursor = await Collection.Database.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = new BsonDocument("name", CollectionName) },
            cancellationToken
        );
        var collection = await cursor.FirstOrDefaultAsync(cancellationToken);
        if (
            collection?.GetValue("options", null) is not BsonDocument options
            || options.GetValue("validator", null) is not BsonDocument validator
            || validator.ElementCount == 0
        )
        {
            return null;
        }

        return new BsonDocument
        {
            { "validator", validator },
            { "validationLevel", options.GetValue("validationLevel", BsonNull.Value) },
            { "validationAction", options.GetValue("validationAction", BsonNull.Value) },
        };
    }

    /// <summary>
    /// The collection's <c>$jsonSchema</c> validator, or null for none (the default). A backstop only: the C# classes
    /// are the main protection.
    /// </summary>
    protected virtual BsonDocument? DefineValidator() => null;

    private static BsonDocument Validation(BsonDocument validator) =>
        new()
        {
            { "validator", validator },
            { "validationLevel", ValidationLevel },
            { "validationAction", ValidationAction },
        };

    // collMod works on a collection that already exists, whatever it holds, and changes none of its documents. The
    // index build normally creates the collection first; if it didn't, it is created with the validator, using the same
    // settings. If another instance creates it in between (NamespaceExists), collMod is simply run again. A failure,
    // such as a database user that may not run collMod, is logged and left for the schema check to report as
    // Degraded: it must never stop the service starting. It is tried again at the next startup.
    private void EnsureValidator()
    {
        if (DefineValidator() is not { } validator)
        {
            return;
        }

        try
        {
            try
            {
                RunWithValidation("collMod", validator);
            }
            catch (MongoCommandException ex) when (ex.Code == NamespaceNotFound)
            {
                try
                {
                    RunWithValidation("create", validator);
                }
                catch (MongoCommandException created) when (created.Code == NamespaceExists)
                {
                    RunWithValidation("collMod", validator);
                }
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Logger.LogWarning(
                ex,
                "The $jsonSchema validator could not be applied to {Collection}; the schema check reports it as Degraded",
                CollectionName
            );
        }
    }

    private void RunWithValidation(string command, BsonDocument validator) =>
        Collection.Database.RunCommand<BsonDocument>(
            new BsonDocument(command, CollectionName).AddRange(Validation(validator))
        );
}
