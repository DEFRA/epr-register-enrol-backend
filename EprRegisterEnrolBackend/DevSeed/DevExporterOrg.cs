using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DevSeed;

/// <summary>
/// A dedicated dev and e2e exporter: an org in the stub ReEx registry
/// (<see cref="Organisation.Services.FakeOrganisationPersistence"/>) that
/// <see cref="AccreditationApplication.Startup.DevExporterAccreditationSeedService"/> gives an Approved current-year
/// registration and its plastic <see cref="Applications"/>, plus an Approved current-year registration and
/// accreditation for its <see cref="Reprocessor"/> if it has one. Both use these values, so the two agree.
/// </summary>
/// <remarks>
/// The frontend mirrors these ids and names in src/server/common/constants/test-orgs.js, and the numbers in its
/// stub (stub-manage-accreditations.js), as do the fe-tests fixtures (test/fixtures/manage-exporter-org.js,
/// in-year-change-org.js and, RA-765, reapply-orgs.js); keep all three in step.
/// </remarks>
internal sealed class DevExporterOrg
{
    /// <summary>
    /// RA-425: the material dashboard pages (/manage) and their ra-425 e2e journey. RA-697: also a glass
    /// reprocessor, and both accreditations have seeded change requests (AccreditationChanges.DevChangeRequestSeedService).
    /// </summary>
    internal static readonly DevExporterOrg Manage = new()
    {
        OrgId = 50018,
        Name = "Manage Accreditation Test Ltd",
        TradingName = "Manage Accreditation Test",
        Email = "info@manageaccreditationtest.co.uk",
        CompaniesHouseNumber = "12345018",
        AddressLine1 = "18 Harbour Road",
        Town = "Felixstowe",
        Postcode = "IP11 3AB",
        Reprocessor = new DevReprocessorRegistration
        {
            OrgId = 50018,
            Material = MaterialType.Glass,
            GlassRecyclingProcess = GlassRecyclingProcess.Remelt,
            Sequence = 2,
            AddressLine1 = "Unit 18, Furnace Lane",
            Town = "Doncaster",
            Postcode = "DN4 5HT",
        },
    };

    /// <summary>
    /// RA-492: the in-year "add a registered ORS to your accreditation" journey and its e2e spec. Its own org, so
    /// that journey's change requests never alter what RA-425's spec sees.
    /// </summary>
    internal static readonly DevExporterOrg InYearChange = new()
    {
        OrgId = 50019,
        Name = "In-Year Change Exports Ltd",
        TradingName = "In-Year Change Exports",
        Email = "info@inyearchangeexports.co.uk",
        CompaniesHouseNumber = "12345019",
        AddressLine1 = "Change House",
        Town = "Hull",
        Postcode = "HU1 1AA",
    };

    /// <summary>RA-697: an Approved exporter with no change requests, for the in-year change pages' empty states.</summary>
    internal static readonly DevExporterOrg NoChangeRequests = new()
    {
        OrgId = 50020,
        Name = "No Change Requests Ltd",
        TradingName = "No Change Requests",
        Email = "info@nochangerequests.co.uk",
        CompaniesHouseNumber = "12345020",
        AddressLine1 = "20 Dock Street",
        Town = "Grimsby",
        Postcode = "DN31 3AA",
    };

    /// <summary>
    /// RA-697: an Approved exporter registration with no accreditation in any year, for the registration-only
    /// manage pages.
    /// </summary>
    internal static readonly DevExporterOrg RegistrationOnly = new()
    {
        OrgId = 50021,
        Name = "Registration Only Exports Ltd",
        TradingName = "Registration Only Exports",
        Email = "info@registrationonlyexports.co.uk",
        CompaniesHouseNumber = "12345021",
        AddressLine1 = "21 Quay Road",
        Town = "Southampton",
        Postcode = "SO14 3AA",
        Applications = [],
    };

    /// <summary>
    /// RA-697: an Approved exporter registration with no accreditation and no registered ORSs, for the
    /// overseas-sites page's empty Accredited and Registered tabs.
    /// </summary>
    internal static readonly DevExporterOrg NoOverseasSites = new()
    {
        OrgId = 50022,
        Name = "No Overseas Sites Exports Ltd",
        TradingName = "No Overseas Sites Exports",
        Email = "info@nooverseassitesexports.co.uk",
        CompaniesHouseNumber = "12345022",
        AddressLine1 = "22 Wharf Street",
        Town = "Liverpool",
        Postcode = "L3 4AA",
        Applications = [],
    };

    // RA-765: four exporters whose applications, by year relative to the clock's (Y), set which accreditation year
    // the manage page's apply banner offers. Their own orgs, so no other spec's data changes what the banner offers.

    /// <summary>RA-765: Approved Y-1 only, so the banner offers the current year.</summary>
    internal static readonly DevExporterOrg ReapplyCurrentYear = new()
    {
        OrgId = 50026,
        Name = "Reapply Current Year Ltd",
        TradingName = "Reapply Current Year",
        Email = "info@reapplycurrentyear.co.uk",
        CompaniesHouseNumber = "12345026",
        AddressLine1 = "26 Quay Street",
        Town = "Ipswich",
        Postcode = "IP3 0AA",
        Applications = [new(-1, ApplicationStatus.Approved)],
    };

    /// <summary>RA-765: Approved Y and Submitted Y+1, so the banner is hidden.</summary>
    internal static readonly DevExporterOrg AppliedNextYear = new()
    {
        OrgId = 50027,
        Name = "Applied Next Year Ltd",
        TradingName = "Applied Next Year",
        Email = "info@appliednextyear.co.uk",
        CompaniesHouseNumber = "12345027",
        AddressLine1 = "27 Quay Street",
        Town = "Ipswich",
        Postcode = "IP3 0AB",
        Applications = [new(0, ApplicationStatus.Approved), new(1, ApplicationStatus.Submitted)],
    };

    /// <summary>
    /// RA-765: Approved Y and Rejected (shown as Refused) Y+1, so the banner offers next year and the apply page
    /// shows the refused application with "start new".
    /// </summary>
    internal static readonly DevExporterOrg RefusedNextYear = new()
    {
        OrgId = 50028,
        Name = "Refused Next Year Ltd",
        TradingName = "Refused Next Year",
        Email = "info@refusednextyear.co.uk",
        CompaniesHouseNumber = "12345028",
        AddressLine1 = "28 Quay Street",
        Town = "Ipswich",
        Postcode = "IP3 0AD",
        Applications = [new(0, ApplicationStatus.Approved), new(1, ApplicationStatus.Rejected)],
    };

    /// <summary>RA-765: Approved Y-2 only (lapsed), so the banner offers the current year, seeded from Y-2.</summary>
    internal static readonly DevExporterOrg LapsedAccreditation = new()
    {
        OrgId = 50029,
        Name = "Lapsed Accreditation Ltd",
        TradingName = "Lapsed Accreditation",
        Email = "info@lapsedaccreditation.co.uk",
        CompaniesHouseNumber = "12345029",
        AddressLine1 = "29 Quay Street",
        Town = "Ipswich",
        Postcode = "IP3 0AE",
        Applications = [new(-2, ApplicationStatus.Approved)],
    };

    /// <summary>RA-765: the dedicated exporters for the manage page's apply banner.</summary>
    // Before All, which reads it: static fields initialise in order.
    internal static readonly IReadOnlyList<DevExporterOrg> Reapply =
    [
        ReapplyCurrentYear,
        AppliedNextYear,
        RefusedNextYear,
        LapsedAccreditation,
    ];

    internal static readonly IReadOnlyList<DevExporterOrg> All =
    [
        Manage,
        InYearChange,
        NoChangeRequests,
        RegistrationOnly,
        NoOverseasSites,
        .. Reapply,
    ];

    private DevExporterOrg() { }

    internal required int OrgId { get; init; }
    internal string OrganisationId => OrgId.ToString(CultureInfo.InvariantCulture);

    // The ReEx registration id, shaped like the stub registry's others, e.g. aaa000000000000000050018.
    internal ObjectId RegistrationId => ObjectId.Parse($"aaa{OrgId:D21}");
    internal required string Name { get; init; }
    internal required string TradingName { get; init; }
    internal required string Email { get; init; }
    internal required string CompaniesHouseNumber { get; init; }
    internal const MaterialType Material = MaterialType.Plastic;
    internal string PermitNumber => $"WML{OrgId}";
    internal required string AddressLine1 { get; init; }
    internal required string Town { get; init; }
    internal required string Postcode { get; init; }
    internal string RegisteredAddress => $"{AddressLine1}, {Town}, {Postcode}";

    /// <summary>
    /// The exporter registration's plastic accreditation applications: by default one Approved for the current year,
    /// and none for an org the seed gives a registration but no accreditation (RA-697).
    /// </summary>
    internal IReadOnlyList<DevExporterApplication> Applications { get; init; } =
        [new(0, ApplicationStatus.Approved)];

    /// <summary>The org's reprocessor registration, if it has one besides its exporter registration.</summary>
    internal DevReprocessorRegistration? Reprocessor { get; init; }

    internal static DevExporterOrg? Find(int orgId) => All.FirstOrDefault(o => o.OrgId == orgId);

    // Both numbers follow the clock, so the stub registry, the registration version and the
    // accreditation agree in any year, e.g. R26EX0500180001PL.
    internal string RegistrationNumberFor(int year) =>
        RegulatoryNumberFormat
            .For(
                new RegulatoryNumberSpec
                {
                    Nation = Nation.England,
                    IsExporter = true,
                    OrgId = OrgId,
                    Material = Material,
                    Year = year,
                }
            )
            .RegistrationNumber(sequence: 1);

    // As ReEx issued the 2026 numbers, the registration's sequence, e.g. A26EX0500180001PL. Every
    // year keeps it, as a continuous renewal would.
    internal string AccreditationNumberFor(int year) =>
        DevAccreditationNumber.For(year, RegistrationNumberFor(year));
}

/// <summary>
/// RA-765: an accreditation application the dev seed gives a dedicated exporter's registration, for the year
/// <paramref name="YearOffset"/> years from the clock's, with <paramref name="Status"/>.
/// </summary>
internal sealed record DevExporterApplication(int YearOffset, ApplicationStatus Status);

/// <summary>
/// RA-697: a dedicated org's reprocessor registration, which the seed accredits alongside its exporter one. Its own
/// ReEx registration id (bbb + org id), registration sequence and application id, so the two never collide.
/// </summary>
internal sealed class DevReprocessorRegistration
{
    internal required int OrgId { get; init; }

    // e.g. bbb000000000000000050018.
    internal ObjectId RegistrationId => ObjectId.Parse($"bbb{OrgId:D21}");
    internal required MaterialType Material { get; init; }
    internal GlassRecyclingProcess? GlassRecyclingProcess { get; init; }

    // The exporter registration is sequence 1.
    internal required int Sequence { get; init; }
    internal required string AddressLine1 { get; init; }
    internal required string Town { get; init; }
    internal required string Postcode { get; init; }
    internal string SiteAddress => $"{AddressLine1}, {Town}, {Postcode}";

    // e.g. R26ER0500180002GR.
    internal string RegistrationNumberFor(int year) =>
        RegulatoryNumberFormat
            .For(
                new RegulatoryNumberSpec
                {
                    Nation = Nation.England,
                    IsExporter = false,
                    OrgId = OrgId,
                    Material = Material,
                    GlassRecyclingProcess = GlassRecyclingProcess,
                    Year = year,
                }
            )
            .RegistrationNumber(Sequence);

    // e.g. A26ER0500180002GR, built as the exporter registration's is.
    internal string AccreditationNumberFor(int year) =>
        DevAccreditationNumber.For(year, RegistrationNumberFor(year));
}

/// <summary>
/// A dev seed's accreditation number: <c>A</c>, the year, then the registration number from its
/// fourth character, so the sequence is the registration's. That is how many 2026 numbers ReEx
/// issued looked, and the operator E2E tests expect these exact numbers. It stands for an
/// accreditation that already exists, not one this service issues: approval issues a new number
/// with <see cref="AccreditationNumberRules"/>, which never reuses the registration's sequence.
/// </summary>
internal static class DevAccreditationNumber
{
    internal static string For(int year, string registrationNumber) =>
        $"A{year % 100:D2}{registrationNumber[3..]}";
}
