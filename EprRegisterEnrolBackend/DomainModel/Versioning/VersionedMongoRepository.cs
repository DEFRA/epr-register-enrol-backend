using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Versioning;

/// <summary>
/// Writes and reads versioned documents held in one collection per entity (design doc §4.2). Each
/// change closes the current version and inserts the next in one transaction, so there is never a gap,
/// an overlap or two current versions. Writes never change the object the caller passes in: the
/// version that was saved is returned instead.
/// </summary>
public class VersionedMongoRepository<T>(
    IMongoClient client,
    IMongoCollection<T> collection,
    TimeProvider clock
)
    where T : VersionedEntityBase
{
    private static FilterDefinitionBuilder<T> Filter => Builders<T>.Filter;

    /// <summary>Writes version 1 of a new entity, minting its <c>entityId</c> unless one is supplied.</summary>
    public async Task<T> InsertAsync(
        T entity,
        ChangeInfo change,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    )
    {
        var version = Stamped(
            entity,
            entity.EntityId == ObjectId.Empty ? ObjectId.GenerateNewId() : entity.EntityId,
            1,
            Now(),
            change
        );
        if (session is null)
        {
            await collection.InsertOneAsync(version, cancellationToken: cancellationToken);
        }
        else
        {
            await collection.InsertOneAsync(session, version, cancellationToken: cancellationToken);
        }

        return version;
    }

    /// <summary>
    /// Replaces the current version of <paramref name="entity"/>'s <c>entityId</c> with a new version, provided
    /// the current version is <paramref name="expectedVersion"/>. Pass a <paramref name="session"/> with a
    /// transaction already started to take part in a larger transaction (for example approval, which touches
    /// several entities); otherwise it runs in its own.
    /// </summary>
    /// <exception cref="VersionConflictException">The current version is not <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="session"/> has no transaction in progress, so the close and the insert would not be atomic.
    /// </exception>
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
            if (!session.IsInTransaction)
            {
                throw new InvalidOperationException(
                    "UpdateAsync needs a session with a transaction in progress: without one, closing the current version and inserting the next are not atomic."
                );
            }

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
        await Find(
                session,
                Filter.Eq(x => x.EntityId, entityId) & Filter.Eq(x => x.IsCurrent, true)
            )
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The current version matching <paramref name="filter"/>, for lookups by business key (for example
    /// <c>reExRegistrationId</c>) that a current-version unique index keeps to one match.
    /// </summary>
    public async Task<T?> FindCurrentAsync(
        FilterDefinition<T> filter,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    ) =>
        await Find(session, filter & Filter.Eq(x => x.IsCurrent, true))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Every current version matching <paramref name="filter"/>, for example all of a registration's ORSs.</summary>
    public async Task<List<T>> FindAllCurrentAsync(
        FilterDefinition<T> filter,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    ) =>
        await Find(session, filter & Filter.Eq(x => x.IsCurrent, true))
            .ToListAsync(cancellationToken);

    /// <summary>The version that was current at <paramref name="at"/>: <c>validFrom &lt;= at &lt; validTo</c>.</summary>
    public async Task<T?> GetAsOfAsync(
        ObjectId entityId,
        DateTime at,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    ) =>
        await Find(
                session,
                Filter.Eq(x => x.EntityId, entityId)
                    & Filter.Lte(x => x.ValidFrom, at)
                    & Filter.Gt(x => x.ValidTo, at)
            )
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Every version, oldest first.</summary>
    public async Task<List<T>> GetVersionsAsync(
        ObjectId entityId,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    ) =>
        await Find(session, Filter.Eq(x => x.EntityId, entityId))
            .Sort(Builders<T>.Sort.Ascending(x => x.Version))
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
                    Filter.Eq(x => x.EntityId, entity.EntityId)
                        & Filter.Eq(x => x.IsCurrent, true)
                        & Filter.Eq(x => x.Version, expectedVersion)
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
            Filter.Eq(x => x.Id, old.Id) & Filter.Eq(x => x.IsCurrent, true),
            Builders<T>.Update.Set(x => x.IsCurrent, false).Set(x => x.ValidTo, now),
            cancellationToken: cancellationToken
        );
        if (closed.MatchedCount == 0)
        {
            throw new VersionConflictException(entity.EntityId, expectedVersion);
        }

        var next = Stamped(entity, old.EntityId, expectedVersion + 1, now, change);
        await collection.InsertOneAsync(session, next, cancellationToken: cancellationToken);
        return new VersionedWriteResult<T>(next, true);
    }

    private IFindFluent<T, T> Find(IClientSessionHandle? session, FilterDefinition<T> filter) =>
        session is null ? collection.Find(filter) : collection.Find(session, filter);

    private static bool SameContent(T old, T proposed) => Content(old) == Content(proposed);

    // The stored form, so values the database would round (sub-millisecond times) compare as stored.
    private static BsonDocument Content(T entity)
    {
        var doc = entity.ToBsonDocument();
        RemoveExcluded(doc, BsonClassMap.LookupClassMap(entity.GetType()));
        return doc;
    }

    private static void RemoveExcluded(BsonDocument doc, BsonClassMap map)
    {
        foreach (var member in map.AllMemberMaps)
        {
            if (member.MemberInfo.IsDefined(typeof(ExcludedFromChangeDetectionAttribute), true))
            {
                doc.Remove(member.ElementName);
            }
            else if (doc.TryGetValue(member.ElementName, out var value))
            {
                RemoveExcluded(value, member.MemberType);
            }
        }
    }

    private static void RemoveExcluded(BsonValue value, Type type)
    {
        if (value is BsonDocument nested && BsonClassMap.IsClassMapRegistered(type))
        {
            RemoveExcluded(nested, BsonClassMap.LookupClassMap(type));
        }
        else if (value is BsonArray items && ItemType(type) is { } itemType)
        {
            foreach (var item in items)
            {
                RemoveExcluded(item, itemType);
            }
        }
    }

    private static Type? ItemType(Type type) =>
        type.GetInterfaces()
            .Prepend(type)
            .FirstOrDefault(t =>
                t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            )
            ?.GetGenericArguments()[0];

    // A copy, so a failed commit never leaves the caller holding a version that was not saved.
    private static T Stamped(
        T entity,
        ObjectId entityId,
        long version,
        DateTime validFrom,
        ChangeInfo change
    )
    {
        var copy = BsonSerializer.Deserialize<T>(entity.ToBsonDocument());
        copy.Id = ObjectId.GenerateNewId();
        copy.EntityId = entityId;
        copy.Version = version;
        copy.ValidFrom = validFrom;
        copy.ValidTo = VersionSentinel.ValidTo;
        copy.IsCurrent = true;
        copy.LastChange = change;
        return copy;
    }

    private DateTime Now()
    {
        var utc = clock.GetUtcNow().UtcDateTime;
        return new DateTime(
            utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond),
            DateTimeKind.Utc
        );
    }
}
