using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>
/// Pages through the legacy applications by <c>_id</c>, reading only the fields asked for, because an old document may
/// not deserialise. Only ObjectId ids are walked: the application classes cannot read any other, so see
/// <see cref="CountOtherIdsAsync"/> for those.
/// </summary>
internal static class LegacyApplicationWalk
{
    private const string Id = "_id";

    private static FilterDefinitionBuilder<BsonDocument> Filter => Builders<BsonDocument>.Filter;

    public static Task<List<BsonDocument>> NextBatchAsync(
        IMongoDbClientFactory clientFactory,
        ObjectId? after,
        int batchSize,
        ProjectionDefinition<BsonDocument> fields,
        CancellationToken cancellationToken
    ) =>
        Legacy(clientFactory)
            .Find(
                Filter.Type(Id, BsonType.ObjectId)
                    & (after is { } id ? Filter.Gt(Id, id) : Filter.Empty)
            )
            .Sort(Builders<BsonDocument>.Sort.Ascending(Id))
            .Limit(batchSize)
            .Project(fields)
            .ToListAsync(cancellationToken);

    public static Task<long> CountOtherIdsAsync(
        IMongoDbClientFactory clientFactory,
        CancellationToken cancellationToken
    ) =>
        Legacy(clientFactory)
            .CountDocumentsAsync(
                Filter.Not(Filter.Type(Id, BsonType.ObjectId)),
                cancellationToken: cancellationToken
            );

    private static IMongoCollection<BsonDocument> Legacy(IMongoDbClientFactory clientFactory) =>
        clientFactory.GetCollection<BsonDocument>(DomainModelCollections.LegacyApplications);
}
