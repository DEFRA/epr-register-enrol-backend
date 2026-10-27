using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Versioning;

// Element names that describe the version, not the entity: ignored when deciding whether an update changed anything.
// Kept out of the generic repository so one array is shared by every closed type.
internal static class VersionMetadata
{
    public static readonly string[] ElementNames =
    [
        "_id",
        "version",
        "validFrom",
        "validTo",
        "isCurrent",
        "lastChange",
    ];
}

/// <summary>
/// Writes and reads versioned documents held in one collection per entity (design doc §4.2). Each
/// change closes the current version and inserts the next in one transaction, so there is never a gap,
/// an overlap or two current versions. Filters address fields by name, so the entity type must derive
/// from <see cref="VersionedEntityBase"/> (or map the same element names).
/// </summary>
public class VersionedMongoRepository<T>(
    IMongoClient client,
    IMongoCollection<T> collection,
    TimeProvider clock
)
    where T : class, IVersionedEntity
{
    public IMongoCollection<T> Collection => collection;

    /// <summary>Writes version 1 of a new entity, minting its <c>entityId</c> unless one is supplied.</summary>
    public async Task<T> InsertAsync(
        T entity,
        ChangeInfo change,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    )
    {
        Stamp(
            entity,
            entity.EntityId == ObjectId.Empty ? ObjectId.GenerateNewId() : entity.EntityId,
            1,
            Now(),
            change
        );
        if (session is null)
        {
            await collection.InsertOneAsync(entity, cancellationToken: cancellationToken);
        }
        else
        {
            await collection.InsertOneAsync(session, entity, cancellationToken: cancellationToken);
        }

        return entity;
    }

    /// <summary>
    /// Replaces the current version of <paramref name="entity"/>'s <c>entityId</c> with a new version, provided
    /// the current version is <paramref name="expectedVersion"/>. Pass a <paramref name="session"/> to take part
    /// in a larger transaction (for example approval, which touches several entities); otherwise it runs in its own.
    /// </summary>
    /// <exception cref="VersionConflictException">The current version is not <paramref name="expectedVersion"/>.</exception>
    public async Task<VersionedWriteResult<T>> UpdateAsync(
        T entity,
        long expectedVersion,
        ChangeInfo change,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    )
    {
        if (session is not null)
        {
            return await UpdateCoreAsync(
                session,
                entity,
                expectedVersion,
                change,
                cancellationToken
            );
        }

        using var own = await client.StartSessionAsync(cancellationToken: cancellationToken);
        // The driver re-runs the callback on a TransientTransactionError, which is what turns two racing
        // writers into one winner plus one VersionConflictException on the re-read.
        return await own.WithTransactionAsync(
            (s, token) => UpdateCoreAsync(s, entity, expectedVersion, change, token),
            cancellationToken: cancellationToken
        );
    }

    public async Task<T?> GetCurrentAsync(
        ObjectId entityId,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    ) =>
        await Find(session, Doc(("entityId", entityId), ("isCurrent", true)))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The version that was current at <paramref name="at"/>: <c>validFrom &lt;= at &lt; validTo</c>.</summary>
    public async Task<T?> GetAsOfAsync(
        ObjectId entityId,
        DateTime at,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    )
    {
        var filter = new BsonDocument
        {
            { "entityId", entityId },
            { "validFrom", new BsonDocument("$lte", at) },
            { "validTo", new BsonDocument("$gt", at) },
        };
        return await Find(session, filter).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Every version, oldest first.</summary>
    public async Task<List<T>> GetVersionsAsync(
        ObjectId entityId,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    ) =>
        await Find(session, Doc(("entityId", entityId)))
            .Sort(Builders<T>.Sort.Ascending("version"))
            .ToListAsync(cancellationToken);

    /// <summary>Current versions that match <paramref name="filter"/>.</summary>
    public async Task<List<T>> FindCurrentAsync(
        FilterDefinition<T> filter,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    ) =>
        await Find(
                session,
                Builders<T>.Filter.And(
                    filter,
                    new BsonDocumentFilterDefinition<T>(Doc(("isCurrent", true)))
                )
            )
            .ToListAsync(cancellationToken);

    private async Task<VersionedWriteResult<T>> UpdateCoreAsync(
        IClientSessionHandle session,
        T entity,
        long expectedVersion,
        ChangeInfo change,
        CancellationToken cancellationToken
    )
    {
        var old =
            await Find(
                    session,
                    Doc(
                        ("entityId", entity.EntityId),
                        ("isCurrent", true),
                        ("version", expectedVersion)
                    )
                )
                .FirstOrDefaultAsync(cancellationToken)
            ?? throw new VersionConflictException(entity.EntityId, expectedVersion);

        if (SameContent(old, entity))
        {
            return new VersionedWriteResult<T>(old, false);
        }

        // Monotonic per entity: BSON dates are millisecond precision, so two writes in one millisecond
        // would otherwise leave a zero-length period.
        var now = Now();
        var earliest = old.ValidFrom.AddMilliseconds(1);
        if (now < earliest)
        {
            now = earliest;
        }

        var closed = await collection.UpdateOneAsync(
            session,
            new BsonDocumentFilterDefinition<T>(Doc(("_id", old.Id), ("isCurrent", true))),
            new BsonDocumentUpdateDefinition<T>(
                new BsonDocument("$set", Doc(("isCurrent", false), ("validTo", now)))
            ),
            cancellationToken: cancellationToken
        );
        if (closed.MatchedCount == 0)
        {
            throw new VersionConflictException(entity.EntityId, expectedVersion);
        }

        Stamp(entity, old.EntityId, expectedVersion + 1, now, change);
        await collection.InsertOneAsync(session, entity, cancellationToken: cancellationToken);
        return new VersionedWriteResult<T>(entity, true);
    }

    private IFindFluent<T, T> Find(IClientSessionHandle? session, BsonDocument filter)
    {
        var definition = new BsonDocumentFilterDefinition<T>(filter);
        return session is null ? collection.Find(definition) : collection.Find(session, definition);
    }

    private IFindFluent<T, T> Find(IClientSessionHandle? session, FilterDefinition<T> filter) =>
        session is null ? collection.Find(filter) : collection.Find(session, filter);

    private static bool SameContent(T old, T proposed) => Content(old) == Content(proposed);

    private static BsonDocument Content(T entity)
    {
        var doc = entity.ToBsonDocument();
        foreach (var name in VersionMetadata.ElementNames)
        {
            doc.Remove(name);
        }

        return doc;
    }

    private static void Stamp(
        T entity,
        ObjectId entityId,
        long version,
        DateTime validFrom,
        ChangeInfo change
    )
    {
        entity.Id = ObjectId.GenerateNewId();
        entity.EntityId = entityId;
        entity.Version = version;
        entity.ValidFrom = validFrom;
        entity.ValidTo = VersionSentinel.ValidTo;
        entity.IsCurrent = true;
        entity.LastChange = change;
    }

    private DateTime Now()
    {
        var utc = clock.GetUtcNow().UtcDateTime;
        return new DateTime(
            utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond),
            DateTimeKind.Utc
        );
    }

    private static BsonDocument Doc(params (string Name, BsonValue Value)[] elements)
    {
        var doc = new BsonDocument();
        foreach (var (name, value) in elements)
        {
            doc.Add(name, value);
        }

        return doc;
    }
}
