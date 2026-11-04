using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DevSeed;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.Registrations;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-425: local manual testing and the fe-tests stack only. Gives the dedicated exporter (stub org
// 50018, Plastic) an Approved accreditation for the current calendar year, and its registration an
// Approved current version in the domain model, so the frontend's material dashboard, manage page
// and overseas-sites page show content. Development-only, registered with DevSeedData:Enabled
// (the local compose stack, alongside the RA-690 Delta Green seeds) or with ConfigKey alone (the
// fe-tests stack, whose other specs must not see the Delta Green data).
//
// The data matches the frontend stub's exporter (stub-manage-accreditations.js): three accredited
// ORSs and one registered only. ORS 001 has a withdrawn interim site, and ORSs 001 and 002 both use
// the same physical site, so the overseas-sites page's per-ORS filtering shows locally. Each copy
// has its own id and number: interim site ids key the edit routes and test ids application-wide,
// and the number allocator keeps numbers unique within an application.
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

    internal const string ExporterOrganisationId = "50018";
    internal const string ExporterOrganisationName = "Manage Accreditation Test Ltd";
    internal const string ExporterCompaniesHouseNumber = "12345018";

    // Fixed so the seeded registration version is recognisable. Nothing reads the Organisation itself yet.
    internal static readonly ObjectId ExporterOrganisationEntityId = ObjectId.Parse(
        "0d0000000000000000050018"
    );
    internal static readonly ObjectId ExporterRegistrationEntityId = ObjectId.Parse(
        "0e0000000000000000050018"
    );

    // R + 2-digit year + agency letter + X (exporter) + 6-digit org id + 4-digit sequence + material.
    // Follows the clock, like the accreditation number, so the stub registry, the registration
    // version and this accreditation agree in any year.
    internal static string ExporterRegistrationNumberFor(int year) =>
        $"R{year % 100:D2}EX0500180001PL";

    protected override async Task SeedCoreAsync(CancellationToken cancellationToken)
    {
        var year = clock.GetUtcNow().Year;
        await SeedAccreditationAsync(year, cancellationToken);
        await SeedRegistrationAsync(year, cancellationToken);
    }

    private async Task SeedAccreditationAsync(int year, CancellationToken cancellationToken)
    {
        var existing = await persistence
            .GetByIdAsync(ExporterOrganisationId, ApplicationIdFor(year).ToString())
            .WaitAsync(cancellationToken);
        if (existing is not null)
        {
            Logger.LogInformation(
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
            Logger.LogWarning(
                "DevExporterAccreditationSeedService: failed to seed the {Year} exporter accreditation",
                year
            );
            return;
        }

        Logger.LogInformation(
            "DevExporterAccreditationSeedService: seeded {Year} exporter accreditation {AccreditationReference}",
            year,
            created.AccreditationReference
        );
    }

    private async Task SeedRegistrationAsync(int year, CancellationToken cancellationToken)
    {
        var seeded = await registrations.SeedIfAbsentAsync(
            ExporterRegistration(year),
            cancellationToken
        );
        Logger.LogInformation(
            seeded
                ? "DevExporterAccreditationSeedService: seeded the exporter's Approved registration version"
                : "DevExporterAccreditationSeedService: exporter registration version already present"
        );
    }

    internal static RegistrationEntity ExporterRegistration(int year) =>
        new()
        {
            EntityId = ExporterRegistrationEntityId,
            OrganisationId = ExporterOrganisationEntityId,
            ReExRegistrationId = FakeOrganisationPersistence.Reg50018.ToString(),
            RegistrationNumber = ExporterRegistrationNumberFor(year),
            Status = RegistrationStatus.Approved,
            WasteProcessingType = WasteProcessingKind.Exporter,
            Material = MaterialType.Plastic,
            Nation = Nation.England,
            Source = new EntitySource { System = SourceSystem.ReEx },
        };

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
            CompaniesHouseNumber = ExporterCompaniesHouseNumber,
            PermitNumbers = ["WML50018"],
            WasteProcessingType = "exporter",
            RegistrationReference = ExporterRegistrationNumberFor(year),
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
