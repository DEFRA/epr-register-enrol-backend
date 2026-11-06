using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DevSeed;

/// <summary>
/// A dedicated dev and e2e exporter: an org in the stub ReEx registry
/// (<see cref="Organisation.Services.FakeOrganisationPersistence"/>) that
/// <see cref="AccreditationApplication.Startup.DevExporterAccreditationSeedService"/> gives an Approved current-year
/// registration and plastic accreditation, plus the same for its <see cref="Reprocessor"/> if it has one. Both use
/// these values, so the two agree.
/// </summary>
/// <remarks>
/// The frontend mirrors these ids and names in src/server/common/constants/test-orgs.js, and the numbers in its
/// stub (stub-manage-accreditations.js), as do the fe-tests fixtures (test/fixtures/manage-exporter-org.js and
/// in-year-change-org.js); keep all three in step.
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

    internal static readonly IReadOnlyList<DevExporterOrg> All = [Manage, InYearChange, NoChangeRequests];

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

    // Derived from the registration number, as a real accreditation number is, e.g. A26EX0500180001PL.
    internal string AccreditationNumberFor(int year) =>
        AccreditationReferenceDerivation
            .Derive(RegistrationNumberFor(year), AccreditationReferenceSourceKind.Registration, year)
            .Reference!;
}

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

    // e.g. A26ER0500180002GR.
    internal string AccreditationNumberFor(int year) =>
        AccreditationReferenceDerivation
            .Derive(RegistrationNumberFor(year), AccreditationReferenceSourceKind.Registration, year)
            .Reference!;
}
