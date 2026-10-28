using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Versioning;

/// <summary>
/// Index definitions shared by every <c>*Versions</c> collection (design doc §4.2, "Indexes per versioned
/// collection"). Every index is named so the schema check can verify it exists.
/// </summary>
public static class VersionedIndexes
{
    /// <summary>The identity indexes: one row per (entity, version), one current version per entity, and as-of reads.</summary>
    public static List<CreateIndexModel<T>> Identity<T>()
        where T : VersionedEntityBase
    {
        var keys = Builders<T>.IndexKeys;
        return
        [
            new(
                keys.Ascending(x => x.EntityId).Ascending(x => x.Version),
                new CreateIndexOptions { Name = "entityId_version_u", Unique = true }
            ),
            new(
                keys.Ascending(x => x.EntityId),
                new CreateIndexOptions<T>
                {
                    Name = "entityId_current_u",
                    Unique = true,
                    PartialFilterExpression = CurrentOnly<T>(),
                }
            ),
            new(
                keys.Ascending(x => x.EntityId)
                    .Ascending(x => x.ValidFrom)
                    .Ascending(x => x.ValidTo),
                new CreateIndexOptions { Name = "entityId_asof" }
            ),
        ];
    }

    /// <summary>Supports "all of this parent's children as of t" reads.</summary>
    public static CreateIndexModel<T> ForeignKeyAsOf<T>(
        string name,
        IndexKeysDefinition<T> foreignKey
    )
        where T : VersionedEntityBase =>
        new(
            foreignKey.Ascending(x => x.ValidFrom).Ascending(x => x.ValidTo),
            new CreateIndexOptions { Name = name }
        );

    /// <summary>
    /// A business-key uniqueness rule that only binds the current version (business keys repeat across
    /// versions). <paramref name="presentWhen"/> restricts the rule to documents that actually carry the
    /// key, so a missing value never collides; it is how "unique sparse" is expressed on a partial index.
    /// </summary>
    public static CreateIndexModel<T> CurrentUnique<T>(
        string name,
        IndexKeysDefinition<T> keys,
        FilterDefinition<T>? presentWhen = null
    )
        where T : VersionedEntityBase =>
        new(
            keys,
            new CreateIndexOptions<T>
            {
                Name = name,
                Unique = true,
                PartialFilterExpression = presentWhen is null
                    ? CurrentOnly<T>()
                    : CurrentOnly<T>() & presentWhen,
            }
        );

    /// <summary>Filter fragment: the field is present as a string.</summary>
    public static FilterDefinition<T> StringPresent<T>(Expression<Func<T, object?>> field) =>
        Builders<T>.Filter.Type(field!, BsonType.String);

    /// <summary>
    /// Filter fragment: the field at a dotted path is present as a string. Only for a path into an array of
    /// subdocuments, which has no typed form.
    /// </summary>
    public static FilterDefinition<T> StringPresent<T>(string dottedPath) =>
        Builders<T>.Filter.Type(dottedPath, BsonType.String);

    private static FilterDefinition<T> CurrentOnly<T>()
        where T : VersionedEntityBase => Builders<T>.Filter.Eq(x => x.IsCurrent, true);
}
