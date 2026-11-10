using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Entities.Sections;
using EprRegisterEnrolBackend.DomainModel.Migration;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolBackend.DomainModel.ReadModel;

/// <summary>
/// Builds the legacy application from the domain model (design doc §5.1, "Facade" column), the reverse of
/// <see cref="LegacyApplicationMapper"/>. It holds no JSON of its own: the result is the same class the existing
/// endpoints serialise. A site's details are its ORS's, overlaid with what this application proposes to change.
/// A field the model does not hold is left empty, and reconciliation reports it.
/// </summary>
public static class ApplicationProjection
{
    public static AccreditationApplicationModel ToLegacy(ApplicationSources sources)
    {
        var application = sources.Application;
        var details = application.AsAccreditation();
        var registration = sources.Registration;
        return new AccreditationApplicationModel
        {
            Id = application.EntityId,
            OrganisationId = application.ReExOrganisationId,
            OrgId = sources.Organisation?.OrganisationNumber,
            OrganisationName = sources.Organisation?.Name,
            CompaniesHouseNumber = sources.Organisation?.CompaniesHouseNumber,
            CompanyRegisteredAddress = sources.Organisation?.FormattedRegisteredAddress,
            CompanyRegisterAddressPostcode = sources.Organisation?.RegisteredAddress?.Postcode,
            IsUkRegisteredAddress = sources.Organisation?.IsUkRegisteredAddress ?? false,
            SubmitterContactDetails = Convert<SubmitterContactDetailsModel>(
                sources.Organisation?.SubmitterContactDetails
            ),
            SiteAddress = registration?.Site?.FormattedAddress,
            PermitNumbers = registration?.PermitNumbers ?? [],
            SourceReExAccreditationId = details.SourceReExAccreditationId,
            SourceYear = details.SeedDataSourceYear,
            PreviousAccreditationNumbers = details.PreviousReservedAccreditationNumbers,
            Year = details.Year,
            RegistrationId = application.ReExRegistrationId,
            IsExporter = registration?.WasteProcessingType == WasteProcessingKind.Exporter,
            WasteProcessingType = registration?.WasteProcessingType.ToString().ToLowerInvariant(),
            Nation = registration?.Nation,
            MaterialType = registration?.Material ?? default,
            GlassRecyclingProcess = registration?.GlassRecyclingProcess,
            ApplicationStatus = application.Status,
            ApplicationReference = application.ApplicationReference,
            PaymentReference = application.PaymentReference,
            CaseManagementWorkItemId = application.CaseManagementWorkItemId,
            RegistrationReference = registration?.RegistrationNumber,
            PreviousRegistrationNumbers = registration?.PreviousRegistrationNumbers ?? [],
            AccreditationReference =
                details.ReservedAccreditationNumber ?? sources.Accreditation?.AccreditationNumber,
            SubmittedBy = Convert<SubmittedByModel>(application.SubmittedBy),
            WithdrawalReason = application.WithdrawalReason,
            DateSent = application.DateSent,
            CaseManagementStatusUpdatedAt = application.CaseManagementStatusUpdatedAt,
            DateLastEdited = application.DateLastEdited,
            Prns = ToPrns(details.Prns),
            BusinessPlan = Convert<AccreditationApplicationBusinessPlan>(details.BusinessPlan)!,
            SamplingPlan = Convert<AccreditationApplicationSamplingPlan>(details.SamplingPlan)!,
            OverseasSites = ToOverseasSites(details, sources.OverseasSites),
            BesEvidence = Convert<AccreditationApplicationBesEvidence>(details.BesEvidence),
            Query = Convert<AccreditationApplicationQuery>(application.Query),
        };
    }

    private static AccreditationApplicationPrns ToPrns(PrnsSection section) =>
        new()
        {
            PlannedTonnageBand = ToLegacyBand(section.PlannedTonnageBand),
            Authorisers = section.Authorisers.ConvertAll(ToAuthoriser),
            SectionStatus = section.SectionStatus,
            Versions = section.Versions.ConvertAll(snapshot => new PrnsSnapshot
            {
                PlannedTonnageBand = ToLegacyBand(snapshot.PlannedTonnageBand),
                Authorisers = snapshot.Authorisers.ConvertAll(ToAuthoriser),
                VersionedAt = snapshot.VersionedAt,
            }),
        };

    private static PrnsAuthoriser ToAuthoriser(Authoriser authoriser) =>
        Convert<PrnsAuthoriser>(authoriser)!;

    // The two enums share their member names; only the stored form differs (D2).
    private static PlannedTonnageBand? ToLegacyBand(TonnageBand? band) =>
        band is { } value ? Enum.Parse<PlannedTonnageBand>(value.ToString()) : null;

    private static AccreditationApplicationOverseasSites? ToOverseasSites(
        AccreditationDetails details,
        IReadOnlyDictionary<ObjectId, OverseasSiteEntity> sites
    ) =>
        details.OverseasSitesSection is not { } section
            ? null
            : new AccreditationApplicationOverseasSites
            {
                Sites = details.ApplicationOverseasSites.ConvertAll(entry =>
                    ToSite(entry, Find(sites, entry))
                ),
                SectionStatus = section.SectionStatus,
                Versions = section.Versions.ConvertAll(snapshot => new OverseasSitesSnapshot
                {
                    Sites = snapshot.Sites.ConvertAll(site => Convert<OverseasSiteModel>(site)!),
                    VersionedAt = snapshot.VersionedAt,
                }),
            };

    // A missing ORS gives an empty site, so reconciliation reports it field by field instead of stopping.
    private static OverseasSiteEntity Find(
        IReadOnlyDictionary<ObjectId, OverseasSiteEntity> sites,
        ApplicationOverseasSiteEntry entry
    ) =>
        entry.OverseasSiteId is { } id && sites.TryGetValue(id, out var site)
            ? site
            : new OverseasSiteEntity();

    private static OverseasSiteModel ToSite(
        ApplicationOverseasSiteEntry entry,
        OverseasSiteEntity ors
    )
    {
        var fields = Overlaid(LegacySiteMapper.Details(ors), entry.ProposedChanges);
        var address = Part<PostalAddress>(fields, "address");
        var contact = Part<ContactPerson>(fields, "contact");
        var codes = Strings(fields, "baselOecdCodes");
        var site = new OverseasSiteModel
        {
            SiteId = entry.SiteId,
            OrsId = ors.OrsId,
            OverseasSiteEntityId = entry.OverseasSiteId,
            SiteName = Text(fields, "siteName") ?? string.Empty,
            SiteAddress = Text(fields, "formattedAddress"),
            AddressLine1 = address?.Line1,
            AddressLine2 = address?.Line2,
            TownOrCity = address?.TownOrCity,
            Country = Text(fields, "country"),
            Coordinates = Text(fields, "coordinates"),
            ContactName = contact?.FullName,
            ContactEmail = contact?.Email,
            ContactPhone = contact?.Phone,
            OperationCodes = Strings(fields, "operationCodes"),
            Code1 = codes.ElementAtOrDefault(0),
            Code2 = codes.ElementAtOrDefault(1),
            Code3 = codes.ElementAtOrDefault(2),
            RepatriatedLoads = Text(fields, "repatriatedLoads"),
            ConditionsOfExport = fields["conditionsOfExport"] is BsonBoolean conditions
                ? conditions.Value
                : null,
            IsEu = fields["isEu"].AsBoolean,
            IsOecd = fields["isOecd"].AsBoolean,
            ValidFrom =
                ors.FirstApprovalSource == FirstApprovalSource.ReEx
                    ? SiteApprovalDate.Format(ors.FirstApprovedAt)
                    : null,
            Selected = entry.Selected,
            BesEvidence = Convert<BesEvidenceModel>(entry.BesEvidence),
            IsNewSite = entry.IsNewSite,
            RegisteredNowAccredited = entry.RegisteredNowAccredited,
            PreviousSites = entry.PreviousSites.ConvertAll(document =>
                BsonSerializer.Deserialize<OverseasSiteModel>(document)
            ),
            InterimSites = entry.InterimSites.ConvertAll(interim => ToInterimSite(interim, ors)),
        };
        InterimSiteSync.SyncMirror(site);
        return site;
    }

    private static InterimSiteModel ToInterimSite(
        ApplicationInterimSiteEntry entry,
        OverseasSiteEntity ors
    )
    {
        var stored =
            ors.InterimSites.Find(i => i.SiteNumber == entry.SiteNumber)
            ?? new InterimSiteEntry { SiteNumber = entry.SiteNumber };
        var fields = Overlaid(LegacySiteMapper.InterimDetails(stored), entry.ProposedChanges);
        var address = Part<PostalAddress>(fields, "address");
        var contact = Part<ContactPerson>(fields, "contact");
        return new InterimSiteModel
        {
            SiteId = entry.LegacySiteId,
            SiteNumber = entry.SiteNumber,
            Country = address?.Country ?? string.Empty,
            SiteName = Text(fields, "siteName") ?? string.Empty,
            AddressLine1 = address?.Line1 ?? string.Empty,
            AddressLine2 = address?.Line2,
            TownOrCity = address?.TownOrCity ?? string.Empty,
            StateOrRegion = address?.Region,
            Postcode = address?.Postcode,
            ContactName = contact?.FullName ?? string.Empty,
            ContactEmail = contact?.Email ?? string.Empty,
            ContactPhone = contact?.Phone ?? string.Empty,
            OperationCodes = Strings(fields, "operationCodes"),
            IsNewSite = entry.IsNewSite,
            CreatedAt = stored.CreatedAt,
            RemovedAt = fields["removedAt"] is BsonDateTime removedAt
                ? removedAt.ToUniversalTime()
                : null,
        };
    }

    private static BsonDocument Overlaid(BsonDocument details, BsonDocument? proposedChanges) =>
        proposedChanges is null
            ? details
            : details.Merge(proposedChanges, overwriteExistingElements: true);

    private static string? Text(BsonDocument fields, string name) =>
        fields[name] is BsonString text ? text.Value : null;

    private static List<string> Strings(BsonDocument fields, string name) =>
        fields[name].AsBsonArray.Select(value => value.AsString).ToList();

    private static T? Part<T>(BsonDocument fields, string name)
        where T : class =>
        fields[name] is BsonDocument part ? BsonSerializer.Deserialize<T>(part) : null;

    // For the shapes whose stored element names match on both sides, so a field added to one is found by reconciliation.
    private static T? Convert<T>(object? source)
        where T : class =>
        source is null
            ? null
            : BsonSerializer.Deserialize<T>(source.ToBsonDocument(source.GetType()));
}
