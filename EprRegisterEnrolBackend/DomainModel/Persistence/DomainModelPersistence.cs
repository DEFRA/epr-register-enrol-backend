using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>
/// Creates a domain model collection's indexes when it is constructed (the base <see cref="MongoService{T}"/>
/// behaviour, which is why these classes derive from it) and reports them for the schema check. Registering the
/// persistence classes is therefore what "expands" the schema in each environment.
/// </summary>
public abstract class DomainModelPersistence<T>(
    IMongoDbClientFactory connectionFactory,
    string collectionName,
    ILoggerFactory loggerFactory
) : MongoService<T>(connectionFactory, collectionName, loggerFactory), IDomainModelCollection
{
    private const int NamespaceNotFound = 26;

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
}
