using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Organisation.Services;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-492: local and e2e data for the in-year "add a registered ORS to your accreditation"
// journey. Gives In-Year Change Exports Ltd (stub org 50018, Plastic exporter) an Approved 2026
// accreditation with one accredited and three registered overseas sites, the registered ones
// missing different combinations of the fields the operator has to supply.
//
// Development-only and opt-in through its own key, separate from DevSeedData:Enabled, so the
// fe-tests stack can turn it on without also seeding Delta Green (org 50003), which other specs
// use. Idempotent: a fixed id means it is inserted once and left alone after that.
public class DevInYearChangeSeedService(
    IAccreditationApplicationPersistence persistence,
    ILogger<DevInYearChangeSeedService> logger
) : BackgroundService
{
    public const string ConfigKey = "DevSeedData:InYearChange";

    internal const string OrganisationId = "50018";
    internal static readonly ObjectId ApplicationId = ObjectId.Parse("a26000000000000000050018");
    internal const string AccreditationReference = "A26ER0500180018PL";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await SeedAsync(stoppingToken);
    }

    internal async Task SeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var existing = await persistence
                .GetByIdAsync(OrganisationId, ApplicationId.ToString())
                .WaitAsync(cancellationToken);
            if (existing is not null)
                return;

            var created = await persistence
                .CreateAsync(Accreditation())
                .WaitAsync(cancellationToken);
            if (created is null)
                logger.LogWarning(
                    "DevInYearChangeSeedService: failed to seed the org 50018 accreditation"
                );
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down before seeding finished - nothing to do.
        }
        catch (Exception ex)
        {
            // Best-effort dev data: a Mongo hiccup must not crash-loop the local backend.
            logger.LogError(ex, "DevInYearChangeSeedService: seeding failed");
        }
    }

    internal static AccreditationApplicationModel Accreditation()
    {
        var approvedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        return new AccreditationApplicationModel
        {
            Id = ApplicationId,
            OrganisationId = OrganisationId,
            OrgId = 50018,
            OrganisationName = "In-Year Change Exports Ltd",
            Year = 2026,
            RegistrationId = FakeOrganisationPersistence.Reg50018.ToString(),
            IsExporter = true,
            Nation = Nation.England,
            CompanyRegisterAddressPostcode = "HU1 1AA",
            CompanyRegisteredAddress = "Change House, Hull, HU1 1AA",
            IsUkRegisteredAddress = true,
            CompaniesHouseNumber = "12345018",
            PermitNumbers = ["WML50018"],
            WasteProcessingType = "exporter",
            RegistrationReference = "R25EX0500180018PL",
            AccreditationReference = AccreditationReference,
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = ApplicationStatus.Approved,
            SourceYear = 2025,
            CreatedAt = approvedAt,
            UpdatedAt = approvedAt,
            DateLastEdited = approvedAt,
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                SectionStatus = SectionStatus.Completed,
                Sites = Sites(),
            },
        };
    }

    private static List<OverseasSiteModel> Sites()
    {
        var accredited = Site(900181, "001", "Hamburg Plastics GmbH", "Germany", isEu: true, isOecd: true);
        accredited.Coordinates = "53.5511, 9.9937";
        accredited.OperationCodes = ["R3"];
        accredited.ConditionsOfExport = true;
        accredited.Selected = true;
        accredited.ValidFrom = "2026-03-01T00:00:00Z";

        var complete = Site(900182, "002", "Lyon Polymer Recycling", "France", isEu: true, isOecd: true);
        complete.Coordinates = "45.7640, 4.8357";
        complete.OperationCodes = ["R3"];
        complete.ConditionsOfExport = true;

        // Country only: coordinates, R code and conditions of export to be supplied, and BES
        // evidence unless conditions of export are met (India is outside the EU and OECD).
        var countryOnly = Site(900183, "003", "Chennai Plastic Works", "India", isEu: false, isOecd: false);

        var noRecyclingDetails = Site(900184, "004", "Izmir Recycling AS", "Turkey", isEu: false, isOecd: true);
        noRecyclingDetails.Coordinates = "38.4237, 27.1428";

        return [accredited, complete, countryOnly, noRecyclingDetails];
    }

    // A registered (not accredited) site with no coordinates, R code or conditions of export.
    private static OverseasSiteModel Site(
        int siteId,
        string orsId,
        string name,
        string country,
        bool isEu,
        bool isOecd
    ) =>
        new()
        {
            SiteId = siteId,
            OrsId = orsId,
            SiteName = name,
            AddressLine1 = $"{siteId} Industrial Estate",
            TownOrCity = name.Split(' ')[0],
            SiteAddress = $"{siteId} Industrial Estate, {name.Split(' ')[0]}, {country}",
            Country = country,
            ContactName = "Site Manager",
            ContactEmail = $"site{siteId}@example.com",
            ContactPhone = "+44 1234 567890",
            OperationCodes = [],
            Code1 = "B3011",
            RepatriatedLoads = "Rejected loads are returned to the UK exporter.",
            IsEu = isEu,
            IsOecd = isOecd,
            Selected = false,
        };
}
