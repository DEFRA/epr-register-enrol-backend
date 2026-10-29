using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Seeding;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>
/// Reshapes a legacy application's overseas and interim sites into ORS entities (design doc §6.1 steps 2 and 3).
/// Status, origin and numbering are decided by <see cref="ApplicationGroupMigrator"/>, not here.
/// </summary>
public static class LegacySiteMapper
{
    /// <summary>A new ORS holding the site's details, with no status, origin or interim sites set.</summary>
    public static OverseasSiteEntity NewOverseasSite(
        ObjectId registrationId,
        OverseasSiteModel site
    )
    {
        var entity = new OverseasSiteEntity { RegistrationId = registrationId };
        SeedEntityMapper.ApplyTo(entity, site);
        entity.Contact = Contact(site.ContactName, site.ContactEmail, site.ContactPhone);
        entity.OperationCodes = [.. site.OperationCodes];
        entity.RepatriatedLoads = site.RepatriatedLoads;
        entity.ConditionsOfExport = site.ConditionsOfExport;
        return entity;
    }

    /// <summary>
    /// The ORS with its details replaced by the site's, keeping what the model owns: identity, status, origin, the
    /// proposing application and the interim sites.
    /// </summary>
    public static OverseasSiteEntity WithDetailsOf(
        OverseasSiteEntity current,
        OverseasSiteModel site
    )
    {
        var next = NewOverseasSite(current.RegistrationId, site);
        next.EntityId = current.EntityId;
        next.Status = current.Status;
        next.Origin = current.Origin;
        next.ProposedByApplicationId = current.ProposedByApplicationId;
        next.InterimSites = current.InterimSites;
        next.Coordinates ??= current.Coordinates;
        return next;
    }

    /// <summary>An interim site entry under <paramref name="siteNumber"/>, with no status set.</summary>
    public static InterimSiteEntry ToInterimSite(InterimSiteModel site, string siteNumber) =>
        new()
        {
            SiteNumber = siteNumber,
            SiteName = site.SiteName,
            Address = new PostalAddress
            {
                Line1 = site.AddressLine1,
                Line2 = site.AddressLine2,
                TownOrCity = site.TownOrCity,
                Region = site.StateOrRegion,
                Postcode = site.Postcode,
                Country = site.Country,
            },
            Contact = Contact(site.ContactName, site.ContactEmail, site.ContactPhone),
            OperationCodes = [.. site.OperationCodes],
            CreatedAt = site.CreatedAt,
            RemovedAt = site.RemovedAt,
        };

    /// <summary>
    /// The ORS fields this application's copy of the site differs on, as the overlay its
    /// <see cref="ApplicationOverseasSiteEntry.ProposedChanges"/> holds; null when it matches.
    /// </summary>
    public static BsonDocument? ProposedChanges(OverseasSiteEntity current, OverseasSiteModel site)
    {
        var proposed = Details(NewOverseasSite(current.RegistrationId, site));
        var stored = Details(current);
        var changes = new BsonDocument(proposed.Where(e => !e.Value.Equals(stored[e.Name])));
        return changes.ElementCount > 0 ? changes : null;
    }

    /// <summary>
    /// The interim site fields this application's copy differs on, withdrawal included, as the overlay its
    /// <see cref="ApplicationInterimSiteEntry.ProposedChanges"/> holds; null when it matches.
    /// </summary>
    public static BsonDocument? InterimProposedChanges(
        InterimSiteEntry current,
        InterimSiteModel site
    )
    {
        var proposed = InterimDetails(ToInterimSite(site, current.SiteNumber));
        var stored = InterimDetails(current);
        var changes = new BsonDocument(proposed.Where(e => !e.Value.Equals(stored[e.Name])));
        return changes.ElementCount > 0 ? changes : null;
    }

    /// <summary>Whether <paramref name="siteNumber"/> is in the current format: exactly three digits, such as <c>001</c>.</summary>
    public static bool IsThreeDigitNumber(string? siteNumber) =>
        siteNumber is { Length: 3 } && siteNumber.All(char.IsAsciiDigit);

    private static BsonDocument Details(OverseasSiteEntity site) =>
        new()
        {
            { "siteName", Value(site.SiteName) },
            { "formattedAddress", Value(site.FormattedAddress) },
            { "address", site.Address?.ToBsonDocument() ?? (BsonValue)BsonNull.Value },
            { "country", Value(site.Country) },
            { "isEu", site.IsEu },
            { "isOecd", site.IsOecd },
            { "coordinates", Value(site.Coordinates) },
            { "contact", site.Contact?.ToBsonDocument() ?? (BsonValue)BsonNull.Value },
            { "operationCodes", new BsonArray(site.OperationCodes) },
            { "baselOecdCodes", new BsonArray(site.BaselOecdCodes) },
            { "repatriatedLoads", Value(site.RepatriatedLoads) },
            {
                "conditionsOfExport",
                site.ConditionsOfExport is { } conditions ? (BsonValue)conditions : BsonNull.Value
            },
        };

    private static BsonDocument InterimDetails(InterimSiteEntry site) =>
        new()
        {
            { "siteName", Value(site.SiteName) },
            { "address", site.Address?.ToBsonDocument() ?? (BsonValue)BsonNull.Value },
            { "contact", site.Contact?.ToBsonDocument() ?? (BsonValue)BsonNull.Value },
            { "operationCodes", new BsonArray(site.OperationCodes) },
            {
                "removedAt",
                site.RemovedAt is { } removedAt ? new BsonDateTime(removedAt) : BsonNull.Value
            },
        };

    private static ContactPerson? Contact(string? name, string? email, string? phone) =>
        string.IsNullOrWhiteSpace(name)
        && string.IsNullOrWhiteSpace(email)
        && string.IsNullOrWhiteSpace(phone)
            ? null
            : new ContactPerson
            {
                FullName = name,
                Email = email,
                Phone = phone,
            };

    private static BsonValue Value(string? value) =>
        value is null ? BsonNull.Value : new BsonString(value);
}
