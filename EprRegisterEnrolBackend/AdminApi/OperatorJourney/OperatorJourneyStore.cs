using System.Text.RegularExpressions;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AdminApi.OperatorJourney;

/// <summary>
/// RA-753: the reads the Operator journey search shares across record types. An organisation's name and
/// number live only in <c>organisations</c>, and every other record reaches its organisation through its
/// registration, so a search by organisation or registration first finds the matching ids (each lookup
/// on its own collection), then filters the record type's collection by them. Every query carries the
/// service-client query time limit, so one that runs too long ends as <c>503 query_timeout</c>.
/// </summary>
public sealed class OperatorJourneyStore(
    IMongoDbClientFactory mongo,
    IOptions<ServiceClientsConfig> config
)
{
    // The stored element names, for the filters that can't be written against the class map.
    private const string OrganisationNumberElement = "organisationNumber";

    private TimeSpan? MaxTime => config.Value.QueryTimeout;

    private FindOptions FindOptions => new() { MaxTime = MaxTime };

    internal IMongoCollection<T> Collection<T>(string name) => mongo.GetCollection<T>(name);

    private IMongoCollection<OrganisationEntity> Organisations =>
        Collection<OrganisationEntity>(DomainModelCollections.Organisations);

    private IMongoCollection<RegistrationEntity> Registrations =>
        Collection<RegistrationEntity>(DomainModelCollections.RegistrationVersions);

    private static FilterDefinition<RegistrationEntity> CurrentRegistrations =>
        Builders<RegistrationEntity>.Filter.Eq(r => r.IsCurrent, true);

    /// <summary><paramref name="text"/> as a case-insensitive "contains" match: matched as text, never as a pattern.</summary>
    internal static BsonRegularExpression Contains(string text) => new(Regex.Escape(text), "i");

    /// <summary>One page of <paramref name="filter"/>'s matches in <paramref name="order"/>, and how many match in all.</summary>
    internal async Task<(IReadOnlyList<T> Page, long Total)> PageAsync<T>(
        string collection,
        FilterDefinition<T> filter,
        SortDefinition<T> order,
        long skip,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var records = Collection<T>(collection);
        var total = await records.CountDocumentsAsync(
            filter,
            new CountOptions { MaxTime = MaxTime },
            cancellationToken
        );
        // A page beyond what Mongo can skip is past the last page, so it is empty.
        if (skip > int.MaxValue)
            return ([], total);

        var page = await records
            .Find(filter, FindOptions)
            .Sort(order)
            .Skip((int)skip)
            .Limit(limit)
            .ToListAsync(cancellationToken);
        return (page, total);
    }

    /// <summary>The organisations whose name or number (as text) contains <paramref name="text"/>, ignoring case.</summary>
    internal async Task<IReadOnlyList<OrganisationEntity>> OrganisationsMatchingAsync(
        string text,
        bool byName,
        bool byNumber,
        CancellationToken cancellationToken
    )
    {
        var filters = Builders<OrganisationEntity>.Filter;
        var matches = new List<FilterDefinition<OrganisationEntity>>();
        if (byName)
            matches.Add(filters.Regex(o => o.Name, Contains(text)));
        if (byNumber)
            matches.Add(NumberContains(text));
        if (matches.Count == 0)
            return [];

        return await Organisations
            .Find(filters.Or(matches), FindOptions)
            .Project<OrganisationEntity>(
                Builders<OrganisationEntity>.Projection.Include(o => o.Id).Include(o => o.ReExOrganisationId)
            )
            .ToListAsync(cancellationToken);
    }

    /// <summary>The organisation number is stored as a number, so it is matched as text with <c>$expr</c>.</summary>
    internal static FilterDefinition<OrganisationEntity> NumberContains(string text) =>
        new BsonDocumentFilterDefinition<OrganisationEntity>(
            new BsonDocument(
                "$expr",
                new BsonDocument(
                    "$regexMatch",
                    new BsonDocument
                    {
                        {
                            "input",
                            new BsonDocument(
                                "$toString",
                                new BsonDocument("$ifNull", new BsonArray { $"${OrganisationNumberElement}", "" })
                            )
                        },
                        { "regex", Regex.Escape(text) },
                        { "options", "i" },
                    }
                )
            )
        );

    /// <summary>
    /// The entity ids of the current registrations whose number contains <paramref name="text"/> (when
    /// <paramref name="byNumber"/>) or that belong to one of <paramref name="organisationIds"/>.
    /// </summary>
    internal async Task<IReadOnlyList<ObjectId>> RegistrationIdsMatchingAsync(
        string text,
        bool byNumber,
        IReadOnlyCollection<ObjectId> organisationIds,
        CancellationToken cancellationToken
    )
    {
        var filters = Builders<RegistrationEntity>.Filter;
        var matches = new List<FilterDefinition<RegistrationEntity>>();
        if (byNumber)
            matches.Add(filters.Regex(r => r.RegistrationNumber, Contains(text)));
        if (organisationIds.Count > 0)
            matches.Add(filters.In(r => r.OrganisationId, organisationIds));
        if (matches.Count == 0)
            return [];
        return await RegistrationIdsAsync(filters.Or(matches), cancellationToken);
    }

    /// <summary>The entity ids of the current registrations that match <paramref name="filter"/>.</summary>
    internal async Task<IReadOnlyList<ObjectId>> RegistrationIdsAsync(
        FilterDefinition<RegistrationEntity> filter,
        CancellationToken cancellationToken
    )
    {
        using var cursor = await Registrations.DistinctAsync(
            r => r.EntityId,
            CurrentRegistrations & filter,
            new DistinctOptions { MaxTime = MaxTime },
            cancellationToken
        );
        return await cursor.ToListAsync(cancellationToken);
    }

    /// <summary>The ids of the organisations with at least one current registration that matches <paramref name="filter"/>.</summary>
    internal async Task<IReadOnlyList<ObjectId>> OrganisationIdsWithRegistrationsAsync(
        FilterDefinition<RegistrationEntity> filter,
        CancellationToken cancellationToken
    )
    {
        using var cursor = await Registrations.DistinctAsync(
            r => r.OrganisationId,
            CurrentRegistrations & filter,
            new DistinctOptions { MaxTime = MaxTime },
            cancellationToken
        );
        return await cursor.ToListAsync(cancellationToken);
    }

    /// <summary>The current version of each of <paramref name="entityIds"/>, by entity id.</summary>
    internal async Task<IReadOnlyDictionary<ObjectId, RegistrationEntity>> CurrentRegistrationsAsync(
        IEnumerable<ObjectId> entityIds,
        CancellationToken cancellationToken
    )
    {
        var ids = entityIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<ObjectId, RegistrationEntity>();
        var found = await Registrations
            .Find(CurrentRegistrations & Builders<RegistrationEntity>.Filter.In(r => r.EntityId, ids), FindOptions)
            .ToListAsync(cancellationToken);
        return found.ToDictionary(r => r.EntityId);
    }

    internal async Task<IReadOnlyDictionary<ObjectId, OrganisationEntity>> OrganisationsAsync(
        IEnumerable<ObjectId> ids,
        CancellationToken cancellationToken
    )
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0)
            return new Dictionary<ObjectId, OrganisationEntity>();
        var found = await Organisations
            .Find(Builders<OrganisationEntity>.Filter.In(o => o.Id, distinct), FindOptions)
            .ToListAsync(cancellationToken);
        return found.ToDictionary(o => o.Id);
    }

    internal async Task<IReadOnlyDictionary<string, OrganisationEntity>> OrganisationsByReExIdAsync(
        IEnumerable<string> reExOrganisationIds,
        CancellationToken cancellationToken
    )
    {
        var distinct = reExOrganisationIds.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
            return new Dictionary<string, OrganisationEntity>(StringComparer.Ordinal);
        var found = await Organisations
            .Find(Builders<OrganisationEntity>.Filter.In(o => o.ReExOrganisationId, distinct), FindOptions)
            .ToListAsync(cancellationToken);
        return found.ToDictionary(o => o.ReExOrganisationId, StringComparer.Ordinal);
    }

    /// <summary>How many current registrations each of <paramref name="organisationIds"/> has; absent when none.</summary>
    internal async Task<IReadOnlyDictionary<ObjectId, int>> RegistrationCountsAsync(
        IEnumerable<ObjectId> organisationIds,
        CancellationToken cancellationToken
    )
    {
        var ids = organisationIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<ObjectId, int>();
        var counts = await Registrations
            .Aggregate(new AggregateOptions { MaxTime = MaxTime })
            .Match(CurrentRegistrations & Builders<RegistrationEntity>.Filter.In(r => r.OrganisationId, ids))
            .Group(r => r.OrganisationId, g => new { OrganisationId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return counts.ToDictionary(c => c.OrganisationId, c => c.Count);
    }

    /// <summary>The distinct countries current overseas sites hold, A to Z. A site with no country adds none.</summary>
    internal async Task<IReadOnlyList<string>> OverseasSiteCountriesAsync(CancellationToken cancellationToken)
    {
        using var cursor = await Collection<OverseasSiteEntity>(DomainModelCollections.OverseasSiteVersions)
            .DistinctAsync(
                s => s.Country,
                Builders<OverseasSiteEntity>.Filter.Eq(s => s.IsCurrent, true),
                new DistinctOptions { MaxTime = MaxTime },
                cancellationToken
            );
        var countries = await cursor.ToListAsync(cancellationToken);
        return countries.OfType<string>().Order(StringComparer.Ordinal).ToList();
    }
}
