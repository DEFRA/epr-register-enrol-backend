using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AdminApi.OperatorJourney;

/// <summary>RA-753: the five record types of AC02, with the filters the ticket lists for each (AC07).</summary>
public static class OperatorJourneyRecordTypes
{
    public static IReadOnlyList<IOperatorJourneyRecordType> All { get; } =
    [
        new AccreditationRecordType(),
        new AccreditationApplicationRecordType(),
        new RegistrationRecordType(),
        new OrganisationRecordType(),
        new OverseasSiteRecordType(),
    ];

    // Search field and filter names shared by several types.
    internal const string AccreditationNumber = "accreditationNumber";
    internal const string RegistrationNumber = "registrationNumber";
    internal const string OrganisationNumber = "organisationNumber";
    internal const string OrganisationName = "organisationName";
    internal const string Status = "status";
    internal const string Year = "year";
    internal const string Material = "material";
    internal const string Nation = "nation";

    internal static SearchFilter MaterialFilter => SearchFilter.EnumNames<MaterialType>(Material, "invalid_material");
    internal static SearchFilter NationFilter => SearchFilter.EnumNames<Nation>(Nation, "invalid_nation");

    /// <summary>
    /// The entity ids of the registrations <c>q</c> reaches through the registration number or the
    /// organisation's name or number, for the record types that hang off a registration.
    /// </summary>
    internal static async Task<IReadOnlyList<ObjectId>> RegistrationIdsForTextAsync(
        OperatorJourneySearch search,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    )
    {
        if (search.Text is not { } text)
            return [];
        var organisations = await store.OrganisationsMatchingAsync(
            text,
            search.Searches(OrganisationName),
            search.Searches(OrganisationNumber),
            cancellationToken
        );
        return await store.RegistrationIdsMatchingAsync(
            text,
            search.Searches(RegistrationNumber),
            organisations.Select(o => o.Id).ToList(),
            cancellationToken
        );
    }

    /// <summary>The current registrations with the given materials and nations (either may be empty, meaning any).</summary>
    internal static FilterDefinition<RegistrationEntity> RegistrationsOf(
        IReadOnlyList<MaterialType> materials,
        IReadOnlyList<Nation> nations
    )
    {
        var filters = Builders<RegistrationEntity>.Filter;
        var filter = filters.Empty;
        if (materials.Count > 0)
            filter &= filters.In(r => r.Material, materials);
        if (nations.Count > 0)
            filter &= filters.In(r => r.Nation, nations.Select(n => (Nation?)n));
        return filter;
    }
}

internal sealed class AccreditationRecordType : VersionedRecordType<AccreditationEntity>
{
    private const string TonnageBand = "tonnageBand";

    public override string Path => "accreditations";
    public override string EndpointName => "SearchOperatorJourneyAccreditations";

    public override IReadOnlyList<string> SearchFields { get; } =
    [
        OperatorJourneySearch.IdField,
        OperatorJourneyRecordTypes.AccreditationNumber,
        OperatorJourneyRecordTypes.RegistrationNumber,
        OperatorJourneyRecordTypes.OrganisationNumber,
        OperatorJourneyRecordTypes.OrganisationName,
    ];

    public override IReadOnlyList<SearchFilter> Filters { get; } =
    [
        SearchFilter.EnumNames<AccreditationStatus>(OperatorJourneyRecordTypes.Status, "invalid_status"),
        SearchFilter.Year(),
        SearchFilter.JsonNames<TonnageBand>(TonnageBand, "invalid_tonnage_band"),
    ];

    protected override string CollectionName => DomainModelCollections.AccreditationVersions;

    protected override async Task<FilterDefinition<AccreditationEntity>> MatchAsync(
        OperatorJourneySearch search,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    )
    {
        var filter = Filter.Empty;
        if (search.Text is { } text)
        {
            var matches = new List<FilterDefinition<AccreditationEntity>>();
            AddIdMatch(search, matches);
            if (search.Searches(OperatorJourneyRecordTypes.AccreditationNumber))
                matches.Add(Filter.Regex(a => a.AccreditationNumber, OperatorJourneyStore.Contains(text)));
            var registrationIds = await OperatorJourneyRecordTypes.RegistrationIdsForTextAsync(
                search,
                store,
                cancellationToken
            );
            if (registrationIds.Count > 0)
                matches.Add(Filter.In(a => a.RegistrationId, registrationIds));
            filter &= AnyOf(matches);
        }

        if (search.Values<AccreditationStatus>(OperatorJourneyRecordTypes.Status) is { Count: > 0 } statuses)
            filter &= Filter.In(a => a.Status, statuses);
        if (search.WholeNumbers(OperatorJourneyRecordTypes.Year) is { Count: > 0 } years)
            filter &= Filter.In(a => a.Year, years);
        if (search.Values(TonnageBand) is { Count: > 0 } bandNames)
        {
            var names = SearchFilter.JsonNamesOf<TonnageBand>();
            filter &= Filter.In(a => a.TonnageBand, bandNames.Select(n => (TonnageBand?)names[n]).Distinct());
        }

        return filter;
    }

    protected override async Task<IReadOnlyList<object>> RowsAsync(
        IReadOnlyList<AccreditationEntity> page,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    )
    {
        var registrations = await store.CurrentRegistrationsAsync(page.Select(a => a.RegistrationId), cancellationToken);
        var rows = page.Select(a =>
                (Accreditation: a, Registration: Linked(registrations, a.RegistrationId, "accreditation", a.EntityId, "registration"))
            )
            .ToList();
        var organisations = await store.OrganisationsAsync(
            rows.Select(r => r.Registration.OrganisationId),
            cancellationToken
        );
        return rows.Select(row =>
                (object)
                    new OperatorJourneyAccreditationDto(
                        row.Accreditation.EntityId.ToString(),
                        row.Accreditation.AccreditationNumber,
                        row.Accreditation.Status,
                        row.Accreditation.Year,
                        row.Accreditation.TonnageBand,
                        row.Registration.EntityId.ToString(),
                        row.Registration.RegistrationNumber,
                        row.Registration.Material,
                        Summary(
                            Linked(
                                organisations,
                                row.Registration.OrganisationId,
                                "registration",
                                row.Registration.EntityId,
                                "organisation"
                            )
                        ),
                        row.Accreditation.ValidFrom
                    )
            )
            .ToList();
    }
}

internal sealed class AccreditationApplicationRecordType : VersionedRecordType<ApplicationEntity>
{
    private const string ApplicationReference = "applicationReference";
    private const string DetailsYear = "details.year";

    public override string Path => "accreditation-applications";
    public override string EndpointName => "SearchOperatorJourneyAccreditationApplications";

    public override IReadOnlyList<string> SearchFields { get; } =
    [
        OperatorJourneySearch.IdField,
        ApplicationReference,
        OperatorJourneyRecordTypes.AccreditationNumber,
        OperatorJourneyRecordTypes.RegistrationNumber,
        OperatorJourneyRecordTypes.OrganisationNumber,
        OperatorJourneyRecordTypes.OrganisationName,
    ];

    public override IReadOnlyList<SearchFilter> Filters { get; } =
    [
        SearchFilter.EnumNames<ApplicationStatus>(OperatorJourneyRecordTypes.Status, "invalid_status"),
        SearchFilter.Year(),
        OperatorJourneyRecordTypes.MaterialFilter,
        OperatorJourneyRecordTypes.NationFilter,
    ];

    protected override string CollectionName => DomainModelCollections.ApplicationVersions;

    // applicationVersions holds every application type; this record type is the accreditation applications.
    protected override FilterDefinition<ApplicationEntity> Scope => base.Scope & ApplicationFields.Accreditations;

    protected override async Task<FilterDefinition<ApplicationEntity>> MatchAsync(
        OperatorJourneySearch search,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    )
    {
        var filter = search.Text is { } text ? await TextMatchAsync(text, search, store, cancellationToken) : Filter.Empty;

        if (search.Values<ApplicationStatus>(OperatorJourneyRecordTypes.Status) is { Count: > 0 } statuses)
            filter &= Filter.In(a => a.Status, statuses);
        if (search.WholeNumbers(OperatorJourneyRecordTypes.Year) is { Count: > 0 } years)
            filter &= Filter.In(DetailsYear, years);

        // Material and nation are the registration's, so an application with none yet matches neither.
        var materials = search.Values<MaterialType>(OperatorJourneyRecordTypes.Material);
        var nations = search.Values<Nation>(OperatorJourneyRecordTypes.Nation);
        if (materials.Count > 0 || nations.Count > 0)
        {
            var registrationIds = await store.RegistrationIdsAsync(
                OperatorJourneyRecordTypes.RegistrationsOf(materials, nations),
                cancellationToken
            );
            filter &= Filter.In(a => a.RegistrationId, registrationIds.Select(id => (ObjectId?)id));
        }

        return filter;
    }

    // q against the application's own references, its registration's number, and the organisation it names.
    private static async Task<FilterDefinition<ApplicationEntity>> TextMatchAsync(
        string text,
        OperatorJourneySearch search,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    )
    {
        var contains = OperatorJourneyStore.Contains(text);
        var matches = new List<FilterDefinition<ApplicationEntity>>();
        AddIdMatch(search, matches);
        if (search.Searches(ApplicationReference))
            matches.Add(Filter.Regex(a => a.ApplicationReference, contains));
        if (search.Searches(OperatorJourneyRecordTypes.AccreditationNumber))
            matches.Add(Filter.Regex(ApplicationFields.ReservedAccreditationNumber, contains));
        if (search.Searches(OperatorJourneyRecordTypes.RegistrationNumber))
        {
            var registrationIds = await store.RegistrationIdsMatchingAsync(text, true, [], cancellationToken);
            if (registrationIds.Count > 0)
                matches.Add(Filter.In(a => a.RegistrationId, registrationIds.Select(id => (ObjectId?)id)));
        }

        // An application names its organisation by ReEx id, and has one even before it has a registration.
        var organisations = await store.OrganisationsMatchingAsync(
            text,
            search.Searches(OperatorJourneyRecordTypes.OrganisationName),
            search.Searches(OperatorJourneyRecordTypes.OrganisationNumber),
            cancellationToken
        );
        if (organisations.Count > 0)
            matches.Add(Filter.In(a => a.ReExOrganisationId, organisations.Select(o => o.ReExOrganisationId)));
        return AnyOf(matches);
    }

    protected override async Task<IReadOnlyList<object>> RowsAsync(
        IReadOnlyList<ApplicationEntity> page,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    )
    {
        var registrations = await store.CurrentRegistrationsAsync(
            page.Where(a => a.RegistrationId is not null).Select(a => a.RegistrationId!.Value),
            cancellationToken
        );
        var organisations = await store.OrganisationsByReExIdAsync(
            page.Select(a => a.ReExOrganisationId),
            cancellationToken
        );
        return page.Select(application =>
            {
                var registration = application.RegistrationId is { } registrationId
                    ? Linked(registrations, registrationId, "application", application.EntityId, "registration")
                    : null;
                var details = (AccreditationDetails)application.Details;
                return (object)
                    new OperatorJourneyApplicationDto(
                        application.EntityId.ToString(),
                        application.ApplicationReference,
                        details.ReservedAccreditationNumber,
                        application.Status,
                        details.Year,
                        registration?.EntityId.ToString(),
                        registration?.RegistrationNumber,
                        registration?.Material,
                        registration?.Nation,
                        Summary(
                            Linked(
                                organisations,
                                application.ReExOrganisationId,
                                "application",
                                application.EntityId,
                                "organisation"
                            )
                        ),
                        application.ValidFrom
                    );
            })
            .ToList();
    }
}

internal sealed class RegistrationRecordType : VersionedRecordType<RegistrationEntity>
{
    public override string Path => "registrations";
    public override string EndpointName => "SearchOperatorJourneyRegistrations";

    public override IReadOnlyList<string> SearchFields { get; } =
    [
        OperatorJourneySearch.IdField,
        OperatorJourneyRecordTypes.RegistrationNumber,
        OperatorJourneyRecordTypes.OrganisationNumber,
        OperatorJourneyRecordTypes.OrganisationName,
    ];

    public override IReadOnlyList<SearchFilter> Filters { get; } =
    [
        SearchFilter.EnumNames<RegistrationStatus>(OperatorJourneyRecordTypes.Status, "invalid_status"),
        OperatorJourneyRecordTypes.MaterialFilter,
        OperatorJourneyRecordTypes.NationFilter,
    ];

    protected override string CollectionName => DomainModelCollections.RegistrationVersions;

    protected override async Task<FilterDefinition<RegistrationEntity>> MatchAsync(
        OperatorJourneySearch search,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    )
    {
        var filter = Filter.Empty;
        if (search.Text is { } text)
        {
            var matches = new List<FilterDefinition<RegistrationEntity>>();
            AddIdMatch(search, matches);
            if (search.Searches(OperatorJourneyRecordTypes.RegistrationNumber))
                matches.Add(Filter.Regex(r => r.RegistrationNumber, OperatorJourneyStore.Contains(text)));
            var organisations = await store.OrganisationsMatchingAsync(
                text,
                search.Searches(OperatorJourneyRecordTypes.OrganisationName),
                search.Searches(OperatorJourneyRecordTypes.OrganisationNumber),
                cancellationToken
            );
            if (organisations.Count > 0)
                matches.Add(Filter.In(r => r.OrganisationId, organisations.Select(o => o.Id)));
            filter &= AnyOf(matches);
        }

        if (search.Values<RegistrationStatus>(OperatorJourneyRecordTypes.Status) is { Count: > 0 } statuses)
            filter &= Filter.In(r => r.Status, statuses);
        return filter
            & OperatorJourneyRecordTypes.RegistrationsOf(
                search.Values<MaterialType>(OperatorJourneyRecordTypes.Material),
                search.Values<Nation>(OperatorJourneyRecordTypes.Nation)
            );
    }

    protected override async Task<IReadOnlyList<object>> RowsAsync(
        IReadOnlyList<RegistrationEntity> page,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    )
    {
        var organisations = await store.OrganisationsAsync(page.Select(r => r.OrganisationId), cancellationToken);
        return page.Select(registration =>
                (object)
                    new OperatorJourneyRegistrationDto(
                        registration.EntityId.ToString(),
                        registration.RegistrationNumber,
                        registration.Status,
                        registration.Material,
                        registration.Nation,
                        registration.WasteProcessingType,
                        Summary(
                            Linked(
                                organisations,
                                registration.OrganisationId,
                                "registration",
                                registration.EntityId,
                                "organisation"
                            )
                        ),
                        registration.ValidFrom
                    )
            )
            .ToList();
    }
}

internal sealed class OrganisationRecordType : OperatorJourneyRecordType<OrganisationEntity>
{
    private const string HasRegistration = "hasRegistration";

    public override string Path => "organisations";
    public override string EndpointName => "SearchOperatorJourneyOrganisations";

    public override IReadOnlyList<string> SearchFields { get; } =
    [
        OperatorJourneySearch.IdField,
        OperatorJourneyRecordTypes.OrganisationNumber,
        OperatorJourneyRecordTypes.OrganisationName,
    ];

    public override IReadOnlyList<SearchFilter> Filters { get; } =
    [
        OperatorJourneyRecordTypes.NationFilter,
        SearchFilter.TrueOrFalse(HasRegistration, "invalid_has_registration"),
    ];

    protected override string CollectionName => DomainModelCollections.Organisations;

    // Organisations are not versioned: every one is searched.
    protected override FilterDefinition<OrganisationEntity> Scope => Filter.Empty;

    protected override SortDefinition<OrganisationEntity> Order =>
        Builders<OrganisationEntity>.Sort.Descending(o => o.UpdatedAt).Descending(o => o.Id);

    protected override async Task<FilterDefinition<OrganisationEntity>> MatchAsync(
        OperatorJourneySearch search,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    )
    {
        var filter = Filter.Empty;
        if (search.Text is { } text)
        {
            var matches = new List<FilterDefinition<OrganisationEntity>>();
            if (search.Searches(OperatorJourneySearch.IdField) && ObjectId.TryParse(text, out var id))
                matches.Add(Filter.Eq(o => o.Id, id));
            if (search.Searches(OperatorJourneyRecordTypes.OrganisationName))
                matches.Add(Filter.Regex(o => o.Name, OperatorJourneyStore.Contains(text)));
            if (search.Searches(OperatorJourneyRecordTypes.OrganisationNumber))
                matches.Add(OperatorJourneyStore.NumberContains(text));
            filter &= AnyOf(matches);
        }

        // A registration's nation is its regulator's; ReEx's organisation-level regulator is not one (RA-526).
        if (search.Values<Nation>(OperatorJourneyRecordTypes.Nation) is { Count: > 0 } nations)
        {
            var organisationIds = await store.OrganisationIdsWithRegistrationsAsync(
                OperatorJourneyRecordTypes.RegistrationsOf([], nations),
                cancellationToken
            );
            filter &= Filter.In(o => o.Id, organisationIds);
        }

        // Both values together are every organisation, the same as neither.
        var hasRegistration = search.Values(HasRegistration).Distinct().ToList();
        if (hasRegistration.Count == 1)
        {
            var withRegistrations = await store.OrganisationIdsWithRegistrationsAsync(
                Builders<RegistrationEntity>.Filter.Empty,
                cancellationToken
            );
            filter &=
                hasRegistration[0] == "true"
                    ? Filter.In(o => o.Id, withRegistrations)
                    : Filter.Nin(o => o.Id, withRegistrations);
        }

        return filter;
    }

    protected override async Task<IReadOnlyList<object>> RowsAsync(
        IReadOnlyList<OrganisationEntity> page,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    )
    {
        var counts = await store.RegistrationCountsAsync(page.Select(o => o.Id), cancellationToken);
        return page.Select(organisation =>
                (object)
                    new OperatorJourneyOrganisationItemDto(
                        organisation.Id.ToString(),
                        Summary(organisation),
                        counts.GetValueOrDefault(organisation.Id),
                        organisation.UpdatedAt
                    )
            )
            .ToList();
    }
}

internal sealed class OverseasSiteRecordType : VersionedRecordType<OverseasSiteEntity>
{
    private const string OrsId = "orsId";
    private const string Country = "country";

    public override string Path => "overseas-sites";
    public override string EndpointName => "SearchOperatorJourneyOverseasSites";

    public override IReadOnlyList<string> SearchFields { get; } =
    [
        OperatorJourneySearch.IdField,
        OrsId,
        OperatorJourneyRecordTypes.RegistrationNumber,
        OperatorJourneyRecordTypes.OrganisationNumber,
        OperatorJourneyRecordTypes.OrganisationName,
    ];

    public override IReadOnlyList<SearchFilter> Filters { get; } =
    [
        SearchFilter.EnumNames<OverseasSiteStatus>(OperatorJourneyRecordTypes.Status, "invalid_status"),
        SearchFilter.ExactText(Country, "invalid_country"),
    ];

    protected override string CollectionName => DomainModelCollections.OverseasSiteVersions;

    protected override async Task<FilterDefinition<OverseasSiteEntity>> MatchAsync(
        OperatorJourneySearch search,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    )
    {
        var filter = Filter.Empty;
        if (search.Text is { } text)
        {
            var matches = new List<FilterDefinition<OverseasSiteEntity>>();
            AddIdMatch(search, matches);
            if (search.Searches(OrsId))
                matches.Add(Filter.Regex(s => s.OrsId, OperatorJourneyStore.Contains(text)));
            var registrationIds = await OperatorJourneyRecordTypes.RegistrationIdsForTextAsync(
                search,
                store,
                cancellationToken
            );
            if (registrationIds.Count > 0)
                matches.Add(Filter.In(s => s.RegistrationId, registrationIds));
            filter &= AnyOf(matches);
        }

        if (search.Values<OverseasSiteStatus>(OperatorJourneyRecordTypes.Status) is { Count: > 0 } statuses)
            filter &= Filter.In(s => s.Status, statuses);
        if (search.Values(Country) is { Count: > 0 } countries)
            filter &= Filter.In(s => s.Country, countries.Distinct(StringComparer.Ordinal));
        return filter;
    }

    protected override async Task<IReadOnlyList<object>> RowsAsync(
        IReadOnlyList<OverseasSiteEntity> page,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    )
    {
        var registrations = await store.CurrentRegistrationsAsync(page.Select(s => s.RegistrationId), cancellationToken);
        var rows = page.Select(s =>
                (Site: s, Registration: Linked(registrations, s.RegistrationId, "overseas site", s.EntityId, "registration"))
            )
            .ToList();
        var organisations = await store.OrganisationsAsync(
            rows.Select(r => r.Registration.OrganisationId),
            cancellationToken
        );
        return rows.Select(row =>
                (object)
                    new OperatorJourneyOverseasSiteDto(
                        row.Site.EntityId.ToString(),
                        row.Site.OrsId,
                        row.Site.SiteName,
                        row.Site.Country,
                        row.Site.Status,
                        row.Registration.EntityId.ToString(),
                        row.Registration.RegistrationNumber,
                        Summary(
                            Linked(
                                organisations,
                                row.Registration.OrganisationId,
                                "registration",
                                row.Registration.EntityId,
                                "organisation"
                            )
                        ),
                        row.Site.ValidFrom
                    )
            )
            .ToList();
    }
}
