using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Organisation.Services;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-425: local manual testing and the fe-tests stack only. Gives the dedicated exporter (stub org
// 50018, Plastic) an Approved accreditation for the current calendar year, so the frontend's
// material dashboard, manage page and overseas-sites page show content. Registered alongside the
// RA-690 DevAccreditationHistorySeedService, under the same Development-only DevSeedData:Enabled
// flag. DevRegistrationSeedService gives the same registration its Approved registration version.
//
// The data matches the frontend stub's exporter (stub-manage-accreditations.js): three accredited
// ORSs and one registered only. ORS 001 has a withdrawn interim site, and an interim site shared
// with ORS 002, so the overseas-sites page's per-ORS filtering shows locally.
//
// The year comes from the clock at startup. Idempotent: the id is fixed for each year, so it is
// inserted once per year and left alone after that, including any changes a tester makes to it.
public class DevExporterAccreditationSeedService(
    IAccreditationApplicationPersistence persistence,
    TimeProvider clock,
    ILogger<DevExporterAccreditationSeedService> logger
) : BackgroundService
{
    internal const string ExporterOrganisationId = "50018";
    internal const string ExporterOrganisationName = "Manage Accreditation Test Ltd";

    // The stub registry's registration number for this registration (FakeOrganisationPersistence).
    internal const string ExporterRegistrationNumber = "R26EX0500180001PL";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is never held up by anything below.
        await Task.Yield();
        await SeedAsync(stoppingToken);
    }

    // Extracted so tests can await it deterministically.
    internal async Task SeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var year = clock.GetUtcNow().Year;
            var existing = await persistence
                .GetByIdAsync(ExporterOrganisationId, ApplicationIdFor(year).ToString())
                .WaitAsync(cancellationToken);
            if (existing is not null)
            {
                logger.LogInformation(
                    "DevExporterAccreditationSeedService: {Year} exporter accreditation already present",
                    year
                );
                return;
            }

            var created = await persistence
                .CreateAsync(ExporterAccreditation(year))
                .WaitAsync(cancellationToken);
            if (created is null)
            {
                logger.LogWarning(
                    "DevExporterAccreditationSeedService: failed to seed the {Year} exporter accreditation",
                    year
                );
                return;
            }

            logger.LogInformation(
                "DevExporterAccreditationSeedService: seeded {Year} exporter accreditation {AccreditationReference}",
                year,
                created.AccreditationReference
            );
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down before seeding finished - nothing to do.
        }
        catch (Exception ex)
        {
            // Best-effort dev data: a Mongo hiccup must not crash-loop the local backend.
            logger.LogError(ex, "DevExporterAccreditationSeedService: seeding failed");
        }
    }

    // "a" + year + padding + org id, e.g. a20260000000000000050018.
    internal static ObjectId ApplicationIdFor(int year) =>
        ObjectId.Parse($"a{year:D4}00000000000000{ExporterOrganisationId}");

    internal static AccreditationApplicationModel ExporterAccreditation(int year)
    {
        var createdAt = new DateTime(year - 1, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        return new AccreditationApplicationModel
        {
            Id = ApplicationIdFor(year),
            OrganisationId = ExporterOrganisationId,
            OrgId = 50018,
            OrganisationName = ExporterOrganisationName,
            Year = year,
            RegistrationId = FakeOrganisationPersistence.Reg50018.ToString(),
            IsExporter = true,
            Nation = Nation.England,
            CompanyRegisterAddressPostcode = "IP11 3AB",
            CompanyRegisteredAddress = "18 Harbour Road, Felixstowe, IP11 3AB",
            CompaniesHouseNumber = "12345018",
            PermitNumbers = ["WML50018"],
            WasteProcessingType = "exporter",
            RegistrationReference = ExporterRegistrationNumber,
            AccreditationReference = $"A{year % 100:D2}EX0500180001PL",
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = ApplicationStatus.Approved,
            SourceYear = year - 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            DateLastEdited = createdAt,
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                SectionStatus = SectionStatus.Completed,
                Sites = OverseasSites(year),
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
                    SharedInterimSite(),
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
                [SharedInterimSite()]
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
            Ors(901807, "004", "Dragon Recyclers", "8 Harbour Avenue", "Vietnam", null, []),
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

    // The same interim site (same SiteId and SiteNumber) serving two ORSs: a separate copy under
    // each, so withdrawing it from one ORS leaves the other untouched.
    private static InterimSiteModel SharedInterimSite() =>
        InterimSite(901803, "002", "Antwerp Transfer Hub", "Belgium");

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
