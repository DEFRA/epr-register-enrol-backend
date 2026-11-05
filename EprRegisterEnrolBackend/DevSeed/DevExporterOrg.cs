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
    internal static readonly DevExporterOrg Manage = new(
        orgId: 50018,
        name: "Manage Accreditation Test Ltd",
        tradingName: "Manage Accreditation Test",
        email: "info@manageaccreditationtest.co.uk",
        companiesHouseNumber: "12345018",
        addressLine1: "18 Harbour Road",
        town: "Felixstowe",
        postcode: "IP11 3AB"
    );

    /// <summary>
    /// RA-492: the in-year "add a registered ORS to your accreditation" journey and its e2e spec. Its own org, so
    /// that journey's change requests never alter what RA-425's spec sees.
    /// </summary>
    internal static readonly DevExporterOrg InYearChange = new(
        orgId: 50019,
        name: "In-Year Change Exports Ltd",
        tradingName: "In-Year Change Exports",
        email: "info@inyearchangeexports.co.uk",
        companiesHouseNumber: "12345019",
        addressLine1: "Change House",
        town: "Hull",
        postcode: "HU1 1AA"
    );

    internal static readonly IReadOnlyList<DevExporterOrg> All = [Manage, InYearChange];

    private DevExporterOrg(
        int orgId,
        string name,
        string tradingName,
        string email,
        string companiesHouseNumber,
        string addressLine1,
        string town,
        string postcode
    )
    {
        OrgId = orgId;
        OrganisationId = orgId.ToString(CultureInfo.InvariantCulture);
        // The ReEx registration id, shaped like the stub registry's others, e.g. aaa000000000000000050018.
        RegistrationId = ObjectId.Parse($"aaa{orgId:D21}");
        Name = name;
        TradingName = tradingName;
        Email = email;
        CompaniesHouseNumber = companiesHouseNumber;
        PermitNumber = $"WML{orgId}";
        AddressLine1 = addressLine1;
        Town = town;
        Postcode = postcode;
        RegisteredAddress = $"{addressLine1}, {town}, {postcode}";
    }

    internal int OrgId { get; }
    internal string OrganisationId { get; }
    internal ObjectId RegistrationId { get; }
    internal string Name { get; }
    internal string TradingName { get; }
    internal string Email { get; }
    internal string CompaniesHouseNumber { get; }
    internal MaterialType Material => MaterialType.Plastic;
    internal string PermitNumber { get; }
    internal string AddressLine1 { get; }
    internal string Town { get; }
    internal string Postcode { get; }
    internal string RegisteredAddress { get; }

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
