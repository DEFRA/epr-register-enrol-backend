using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DevSeed;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Registrations;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-425: local manual testing and the fe-tests stack only. Gives each dedicated exporter
// (DevExporterOrg: stub orgs 50018 and, for RA-492, 50019, Plastic) an Approved accreditation for
// the current calendar year, and its registration an Approved current version in the domain model,
// so the frontend's material dashboard, manage page and overseas-sites page show content. Development-only, registered with DevSeedData:Enabled
// (the local compose stack, alongside the RA-690 Delta Green seeds) or with ConfigKey alone (the
// fe-tests stack, whose other specs must not see the Delta Green data).
//
// Org 50018's data matches the frontend stub's exporter (stub-manage-accreditations.js): three
// accredited ORSs and one registered only. ORS 001 has a withdrawn interim site, and ORSs 001 and 002 both use
// the same physical site, so the overseas-sites page's per-ORS filtering shows locally. The
// registered-only ORS 004 has an interim site too, which the page must not show. Each copy
// has its own id and number: interim site ids key the edit routes and test ids application-wide,
// and the number allocator keeps numbers unique within an application. Org 50019 (RA-492) has one
// accredited ORS and three registered only, each lacking a different set of the fields the in-year
// "add a registered ORS to your accreditation" journey asks for.
//
// The year comes from the clock at startup. Idempotent: the accreditation's id is fixed for each
// year, so it is inserted once per year and left alone after that, including any changes a tester
// makes to it; a registration that already has a current version is left alone.
public class DevExporterAccreditationSeedService(
    IAccreditationApplicationPersistence persistence,
    RegistrationVersionStore registrations,
    TimeProvider clock,
    ILogger<DevExporterAccreditationSeedService> logger
) : DevSeedService(logger)
{
    public const string ConfigKey = "DevSeedData:ManageExporterEnabled";

    /// <summary>
    /// Whether this seed runs, which is also whether the stub ReEx registry has the dedicated exporters at all:
    /// anywhere else they would be exporters with no accreditation.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        environment.IsDevelopment()
        && (
            configuration.GetValue<bool>(DevAccreditationHistorySeedService.ConfigKey)
            || configuration.GetValue<bool>(ConfigKey)
        );

    // Each dedicated exporter's ORSs, by the year they are accredited from.
    private static readonly Dictionary<DevExporterOrg, Func<int, List<OverseasSiteModel>>> SitesFor =
        new()
        {
            [DevExporterOrg.Manage] = OverseasSites,
            [DevExporterOrg.InYearChange] = InYearChangeOverseasSites,
        };

    protected override async Task SeedCoreAsync(CancellationToken cancellationToken)
    {
        var year = clock.GetUtcNow().Year;
        foreach (var org in DevExporterOrg.All)
        {
            await SeedAccreditationAsync(org, year, cancellationToken);
            await SeedRegistrationAsync(org, year, cancellationToken);
        }
    }

    private async Task SeedAccreditationAsync(
        DevExporterOrg org,
        int year,
        CancellationToken cancellationToken
    )
    {
        var existing = await persistence
            .GetByIdAsync(org.OrganisationId, ApplicationIdFor(org, year).ToString())
            .WaitAsync(cancellationToken);
        if (existing is not null)
        {
            Logger.LogInformation(
                "DevExporterAccreditationSeedService: {Year} exporter accreditation for org {OrgId} already present",
                year,
                org.OrgId
            );
            return;
        }

        var created = await persistence
            .CreateAsync(ExporterAccreditation(org, year))
            .WaitAsync(cancellationToken);
        if (created is null)
        {
            Logger.LogWarning(
                "DevExporterAccreditationSeedService: failed to seed the {Year} exporter accreditation for org {OrgId}",
                year,
                org.OrgId
            );
            return;
        }

        Logger.LogInformation(
            "DevExporterAccreditationSeedService: seeded {Year} exporter accreditation {AccreditationReference}",
            year,
            created.AccreditationReference
        );
    }

    private async Task SeedRegistrationAsync(
        DevExporterOrg org,
        int year,
        CancellationToken cancellationToken
    )
    {
        var seeded = await registrations.SeedIfAbsentAsync(
            ExporterRegistration(org, year),
            cancellationToken
        );
        Logger.LogInformation(
            seeded
                ? "DevExporterAccreditationSeedService: seeded org {OrgId}'s Approved registration version"
                : "DevExporterAccreditationSeedService: org {OrgId}'s registration version already present",
            org.OrgId
        );
    }

    internal static RegistrationEntity ExporterRegistration(DevExporterOrg org, int year) =>
        RegistrationVersionDevSeed.ApprovedRegistration(
            org.OrgId,
            org.RegistrationId,
            org.RegistrationNumberFor(year),
            WasteProcessingKind.Exporter,
            DevExporterOrg.Material
        );

    // "a" + year + padding + org id, e.g. a20260000000000000050018.
    internal static ObjectId ApplicationIdFor(DevExporterOrg org, int year) =>
        ObjectId.Parse($"a{year:D4}{org.OrgId:D19}");

    internal static AccreditationApplicationModel ExporterAccreditation(DevExporterOrg org, int year)
    {
        var createdAt = new DateTime(year - 1, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        return new AccreditationApplicationModel
        {
            Id = ApplicationIdFor(org, year),
            OrganisationId = org.OrganisationId,
            OrgId = org.OrgId,
            OrganisationName = org.Name,
            Year = year,
            RegistrationId = org.RegistrationId.ToString(),
            IsExporter = true,
            Nation = Nation.England,
            CompanyRegisterAddressPostcode = org.Postcode,
            CompanyRegisteredAddress = org.RegisteredAddress,
            CompaniesHouseNumber = org.CompaniesHouseNumber,
            PermitNumbers = [org.PermitNumber],
            WasteProcessingType = "exporter",
            RegistrationReference = org.RegistrationNumberFor(year),
            AccreditationReference = org.AccreditationNumberFor(year),
            MaterialType = DevExporterOrg.Material,
            ApplicationStatus = ApplicationStatus.Approved,
            SourceYear = year - 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            DateLastEdited = createdAt,
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                SectionStatus = SectionStatus.Completed,
                Sites = SitesFor[org](year),
            },
        };
    }

    private static List<OverseasSiteModel> OverseasSites(int year)
    {
        var accreditedFrom = $"{year}-01-01T00:00:00Z";
        var removed = InterimSite(901804, "003", "Old Depot GmbH", "Germany");
        removed.RemovedAt = new DateTime(year, 2, 1, 9, 0, 0, DateTimeKind.Utc);
        List<OverseasSiteModel> sites =
        [
            Ors(
                901801,
                "001",
                "Rotterdam Recycling BV",
                "Industrieweg 44",
                "Netherlands",
                accreditedFrom,
                [
                    InterimSite(901802, "001", "Calais Storage SARL", "France", ["R13"]),
                    AntwerpInterimSite(901803, "002"),
                    removed,
                ]
            ),
            Ors(
                901805,
                "002",
                "Bharat Recycling",
                "12 Ring Road",
                "India",
                accreditedFrom,
                [AntwerpInterimSite(901808, "004")]
            ),
            Ors(
                901806,
                "003",
                "Berlin Steel GmbH",
                "Hafenstrasse 9",
                "Germany",
                accreditedFrom,
                []
            ),
            // Registered only, but its registration lists an interim site: the overseas-sites page
            // must still show no interim sites for it, as they belong to the accreditation.
            Ors(
                901807,
                "004",
                "Dragon Recyclers",
                "8 Harbour Avenue",
                "Vietnam",
                null,
                [InterimSite(901809, "005", "Saigon Port Storage", "Vietnam")]
            ),
        ];
        sites.ForEach(InterimSiteSync.SyncMirror);
        return sites;
    }

    // validFrom null means registered on the registration but not accredited.
    private static OverseasSiteModel Ors(
        int siteId,
        string orsId,
        string siteName,
        string addressLine1,
        string country,
        string? validFrom,
        List<InterimSiteModel> interimSites
    ) =>
        new()
        {
            SiteId = siteId,
            OrsId = orsId,
            SiteName = siteName,
            AddressLine1 = addressLine1,
            TownOrCity = "Port",
            Country = country,
            SiteAddress = $"{addressLine1}, {country}",
            OperationCodes = ["R3"],
            ValidFrom = validFrom,
            Selected = validFrom is not null,
            IsNewSite = false,
            InterimSites = interimSites,
        };

    // RA-492 (org 50019): one accredited ORS with every field the in-year add journey asks for,
    // and three registered only: complete (France: EU and OECD, so no BES evidence), country only
    // (India: outside the EU and OECD, so BES evidence unless the conditions of export are met), and
    // coordinates only (Turkey: OECD, so no BES evidence).
    private static List<OverseasSiteModel> InYearChangeOverseasSites(int year) =>
        [
            InYearChangeOrs(
                Ors(901901, "001", "Hamburg Kunststoff GmbH", "901901 Industrial Estate", "Germany", $"{year}-01-01T00:00:00Z", []),
                isEu: true,
                isOecd: true,
                coordinates: "53.5511, 9.9937",
                operationCodes: ["R3"],
                conditionsOfExport: true
            ),
            InYearChangeOrs(
                Ors(901910, "005", "Lyon Plastiques SA", "901910 Industrial Estate", "France", null, []),
                isEu: true,
                isOecd: true,
                coordinates: "45.7640, 4.8357",
                operationCodes: ["R3"],
                conditionsOfExport: true
            ),
            InYearChangeOrs(
                Ors(901911, "006", "Delhi Polymers Pvt", "901911 Industrial Estate", "India", null, []),
                isEu: false,
                isOecd: false
            ),
            InYearChangeOrs(
                Ors(901912, "007", "Izmir Recycling AS", "901912 Industrial Estate", "Turkey", null, []),
                isEu: false,
                isOecd: true,
                coordinates: "38.4237, 27.1428"
            ),
        ];

    // The contact and waste details a registration holds, and only the given coordinates, R codes
    // and conditions of export.
    private static OverseasSiteModel InYearChangeOrs(
        OverseasSiteModel site,
        bool isEu,
        bool isOecd,
        string? coordinates = null,
        List<string>? operationCodes = null,
        bool? conditionsOfExport = null
    )
    {
        site.IsEu = isEu;
        site.IsOecd = isOecd;
        site.Coordinates = coordinates;
        site.OperationCodes = operationCodes ?? [];
        site.ConditionsOfExport = conditionsOfExport;
        site.ContactName = "Site Manager";
        site.ContactEmail = $"site{site.SiteId}@example.com";
        site.ContactPhone = "+44 1234 567890";
        site.Code1 = "B3011";
        site.RepatriatedLoads = "Rejected loads are returned to the UK exporter.";
        return site;
    }

    // One physical site serving two ORSs: a separate copy under each, so withdrawing it from one ORS
    // leaves the other untouched.
    private static InterimSiteModel AntwerpInterimSite(int siteId, string siteNumber) =>
        InterimSite(siteId, siteNumber, "Antwerp Transfer Hub", "Belgium");

    private static InterimSiteModel InterimSite(
        int siteId,
        string siteNumber,
        string siteName,
        string country,
        List<string>? operationCodes = null
    ) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = siteNumber,
            SiteName = siteName,
            Country = country,
            AddressLine1 = "1 Quay Street",
            TownOrCity = "Port",
            ContactName = "Alex Morgan",
            ContactEmail = "alex.morgan@example.com",
            ContactPhone = "0123456789",
            OperationCodes = operationCodes ?? ["R3", "R13"],
        };
}
