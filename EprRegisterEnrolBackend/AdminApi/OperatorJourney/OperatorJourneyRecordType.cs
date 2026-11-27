using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AdminApi.OperatorJourney;

/// <summary>
/// RA-753: one record type of the Operator journey search. The endpoint (<see cref="OperatorJourneyEndpoints"/>)
/// parses and checks the query against <see cref="SearchFields"/> and <see cref="Filters"/>, the shared
/// pipeline here pages through the matches, and each type says only what is its own: its collection, how
/// <c>q</c> and its filters match, its order, and its result row. A new record type is one more subclass,
/// listed in <see cref="OperatorJourneyRecordTypes.All"/>.
/// </summary>
public abstract class OperatorJourneyRecordType
{
    /// <summary>The path segment under <c>/admin/operator-journey</c>, which is also the Admin UI's <c>type</c>.</summary>
    public abstract string Path { get; }

    /// <summary>The endpoint's name, for the OpenAPI document and route lookups.</summary>
    public abstract string EndpointName { get; }

    /// <summary>The <c>searchFields</c> values, in the spec's order. <c>q</c> searches them all when none is given.</summary>
    public abstract IReadOnlyList<string> SearchFields { get; }

    public abstract IReadOnlyList<SearchFilter> Filters { get; }

    internal abstract Task<(IReadOnlyList<object> Items, long Total)> SearchAsync(
        OperatorJourneySearch search,
        OperatorJourneyStore store,
        long skip,
        int limit,
        CancellationToken cancellationToken
    );
}

/// <summary>The shared pipeline: scope, then the type's match, counted and paged in the type's order, then each row built.</summary>
public abstract class OperatorJourneyRecordType<TEntity> : OperatorJourneyRecordType
{
    protected static FilterDefinitionBuilder<TEntity> Filter => Builders<TEntity>.Filter;

    protected abstract string CollectionName { get; }

    /// <summary>What every search of this type is limited to, such as current versions only.</summary>
    protected abstract FilterDefinition<TEntity> Scope { get; }

    /// <summary>Most recently changed first, then by id, so the order is stable across pages.</summary>
    protected abstract SortDefinition<TEntity> Order { get; }

    /// <summary>The type's match for <paramref name="search"/>'s <c>q</c> and filters, ANDed together.</summary>
    protected abstract Task<FilterDefinition<TEntity>> MatchAsync(
        OperatorJourneySearch search,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    );

    /// <summary>The result rows for one page, in the page's order.</summary>
    protected abstract Task<IReadOnlyList<object>> RowsAsync(
        IReadOnlyList<TEntity> page,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    );

    internal sealed override async Task<(IReadOnlyList<object> Items, long Total)> SearchAsync(
        OperatorJourneySearch search,
        OperatorJourneyStore store,
        long skip,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var filter = Scope & await MatchAsync(search, store, cancellationToken);
        var (page, total) = await store.PageAsync(CollectionName, filter, Order, skip, limit, cancellationToken);
        return (await RowsAsync(page, store, cancellationToken), total);
    }

    /// <summary>Matches when any of <paramref name="matches"/> does; matches nothing when there are none.</summary>
    protected static FilterDefinition<TEntity> AnyOf(IReadOnlyCollection<FilterDefinition<TEntity>> matches) =>
        matches.Count == 0
            ? new BsonDocumentFilterDefinition<TEntity>(new BsonDocument("_id", new BsonDocument("$exists", false)))
            : Filter.Or(matches);

    protected static OperatorJourneyOrganisationDto Summary(OrganisationEntity organisation) =>
        new(organisation.Id.ToString(), organisation.Name, organisation.OrganisationNumber);

    protected static TValue Linked<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> found,
        TKey id,
        string recordType,
        ObjectId recordId,
        string link
    )
        where TKey : notnull =>
        found.TryGetValue(id, out var value)
            ? value
            : throw new OperatorJourneyLinkMissingException(recordType, recordId, link, id.ToString()!);
}

/// <summary>A versioned record type: only current versions, most recently written first.</summary>
public abstract class VersionedRecordType<TEntity> : OperatorJourneyRecordType<TEntity>
    where TEntity : VersionedEntityBase
{
    protected override FilterDefinition<TEntity> Scope => Filter.Eq(x => x.IsCurrent, true);

    protected override SortDefinition<TEntity> Order =>
        Builders<TEntity>.Sort.Descending(x => x.ValidFrom).Descending(x => x.EntityId);

    /// <summary><c>q</c> as an exact entity id, when the <c>id</c> field is searched and <c>q</c> is one.</summary>
    protected static void AddIdMatch(
        OperatorJourneySearch search,
        List<FilterDefinition<TEntity>> matches
    )
    {
        if (search.Searches(OperatorJourneySearch.IdField) && ObjectId.TryParse(search.Text, out var id))
            matches.Add(Filter.Eq(x => x.EntityId, id));
    }
}

/// <summary>
/// A stored record links to a registration or organisation that does not exist. The search can't show
/// the record's organisation, so it fails rather than leave it out or make one up.
/// </summary>
public sealed class OperatorJourneyLinkMissingException(
    string recordType,
    ObjectId recordId,
    string link,
    string linkedId
) : Exception($"{recordType} {recordId} links to {link} {linkedId}, which does not exist.")
{
    public string RecordType { get; } = recordType;
    public ObjectId RecordId { get; } = recordId;
    public string Link { get; } = link;
    public string LinkedId { get; } = linkedId;
}
