using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DevSeed;

/// <summary>
/// RA-425: the dedicated exporter for the material dashboard pages (stub org 50018). Its entry in the stub ReEx
/// registry (<see cref="Organisation.Services.FakeOrganisationPersistence"/>) and the data
/// <see cref="AccreditationApplication.Startup.DevExporterAccreditationSeedService"/> seeds for it both use these,
/// so the two agree.
/// </summary>
/// <remarks>
/// The frontend mirrors these ids and names in src/server/common/constants/test-orgs.js, and the numbers in its
/// stub (stub-manage-accreditations.js); keep the two in step.
/// </remarks>
internal static class ManageExporterOrg
{
    internal const int OrgId = 50018;
    internal static readonly string OrganisationId = OrgId.ToString(CultureInfo.InvariantCulture);

    // The ReEx registration id, shaped like the stub registry's others, e.g. aaa000000000000000050018.
    internal static readonly ObjectId RegistrationId = ObjectId.Parse($"aaa{OrgId:D21}");

    internal const string Name = "Manage Accreditation Test Ltd";
    internal const string CompaniesHouseNumber = "12345018";
    internal const MaterialType Material = MaterialType.Plastic;
    internal static readonly string PermitNumber = $"WML{OrgId}";

    internal const string AddressLine1 = "18 Harbour Road";
    internal const string Town = "Felixstowe";
    internal const string Postcode = "IP11 3AB";
    internal const string RegisteredAddress = $"{AddressLine1}, {Town}, {Postcode}";

    // Both numbers follow the clock, so the stub registry, the registration version and the
    // accreditation agree in any year, e.g. R26EX0500180001PL.
    internal static string RegistrationNumberFor(int year) =>
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
    internal static string AccreditationNumberFor(int year) =>
        AccreditationReferenceDerivation
            .Derive(RegistrationNumberFor(year), AccreditationReferenceSourceKind.Registration, year)
            .Reference!;
}
