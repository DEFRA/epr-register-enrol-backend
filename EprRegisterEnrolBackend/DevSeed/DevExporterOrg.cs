using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DevSeed;

/// <summary>
/// A dedicated dev and e2e exporter: an org in the stub ReEx registry
/// (<see cref="Organisation.Services.FakeOrganisationPersistence"/>) that
/// <see cref="AccreditationApplication.Startup.DevExporterAccreditationSeedService"/> gives an Approved current-year
/// registration and plastic accreditation. Both use these values, so the two agree.
/// </summary>
/// <remarks>
/// The frontend mirrors these ids and names in src/server/common/constants/test-orgs.js, and the numbers in its
/// stub (stub-manage-accreditations.js); keep the two in step.
/// </remarks>
internal sealed class DevExporterOrg
{
    /// <summary>RA-425: the material dashboard pages (/manage) and their ra-425 e2e journey.</summary>
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

    internal static readonly IReadOnlyList<DevExporterOrg> All = [Manage, InYearChange];

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
