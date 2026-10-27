using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Versioning;

/// <summary>
/// Index definitions shared by every <c>*Versions</c> collection (design doc §4.2, "Indexes per versioned
/// collection"). Every index is named so the schema health check can verify it exists.
/// </summary>
public static class VersionedIndexes
{
    private static readonly BsonDocument s_currentOnly = new("isCurrent", true);

    /// <summary>The identity indexes: one row per (entity, version), one current version per entity, and as-of reads.</summary>
    public static List<CreateIndexModel<T>> Identity<T>()
    {
        var keys = Builders<T>.IndexKeys;
        return
        [
            new(keys.Ascending("entityId").Ascending("version"),
                new CreateIndexOptions { Name = "entityId_version_u", Unique = true }),
            new(keys.Ascending("entityId"),
                new CreateIndexOptions<T>
                {
                    Name = "entityId_current_u",
                    Unique = true,
                    PartialFilterExpression = new BsonDocumentFilterDefinition<T>(s_currentOnly),
                }),
            new(keys.Ascending("entityId").Ascending("validFrom").Ascending("validTo"),
                new CreateIndexOptions { Name = "entityId_asof" }),
        ];
    }

    /// <summary>Supports "all of this parent's children as of t" reads.</summary>
    public static CreateIndexModel<T> ForeignKeyAsOf<T>(string foreignKey) =>
        new(Builders<T>.IndexKeys.Ascending(foreignKey).Ascending("validFrom").Ascending("validTo"),
            new CreateIndexOptions { Name = $"{foreignKey}_asof" });

    /// <summary>
    /// A business-key uniqueness rule that only binds the current version (business keys repeat across
    /// versions). <paramref name="presentWhen"/> restricts the rule to documents that actually carry the
    /// key, so a missing value never collides; it is how "unique sparse" is expressed on a partial index.
    /// </summary>
    public static CreateIndexModel<T> CurrentUnique<T>(
        string name,
        IndexKeysDefinition<T> keys,
        BsonDocument? presentWhen = null)
    {
        var filter = new BsonDocument("isCurrent", true);
        if (presentWhen is not null)
        {
            filter.AddRange(presentWhen);
        }

        return new CreateIndexModel<T>(
            keys,
            new CreateIndexOptions<T>
            {
                Name = name,
                Unique = true,
                PartialFilterExpression = new BsonDocumentFilterDefinition<T>(filter),
            });
    }

    /// <summary>Filter fragment: the field is present as a string.</summary>
    public static BsonDocument StringPresent(string field) => new(field, new BsonDocument("$type", "string"));
}
