using EprRegisterEnrolBackend.DomainModel.Migration;

namespace EprRegisterEnrolBackend.DomainModel.Reconciliation;

/// <summary>
/// Which legacy fields each kind of conflict explains, so a conflict only accepts the differences it accounts for and
/// cannot hide unrelated drift on the same application. A kind not listed here explains no field.
/// </summary>
internal static class ConflictExplanations
{
    private const string Site = "overseasSites.sites[].";

    private static readonly string[] s_siteDetails = new[]
    {
        "siteName",
        "siteAddress",
        "addressLine1",
        "addressLine2",
        "townOrCity",
        "country",
        "coordinates",
        "contactName",
        "contactEmail",
        "contactPhone",
        "operationCodes",
        "code1",
        "code2",
        "code3",
        "repatriatedLoads",
        "conditionsOfExport",
        "isEu",
        "isOecd",
    }
        .Select(field => Site + field)
        .ToArray();

    private static readonly string[] s_interimSiteDetails = new[]
    {
        "siteName",
        "country",
        "addressLine1",
        "addressLine2",
        "townOrCity",
        "stateOrRegion",
        "postcode",
        "contactName",
        "contactEmail",
        "contactPhone",
        "operationCodes",
        "removedAt",
    }
        .SelectMany(field =>
            new[] { $"{Site}interimSites[].{field}", $"{Site}interimSite.{field}" }
        )
        .ToArray();

    private static readonly Dictionary<MigrationConflictKind, string[]> s_fields = new()
    {
        [MigrationConflictKind.OrsDetailsDiffer] = s_siteDetails,
        [MigrationConflictKind.ReExDetailsDiffer] = s_siteDetails,
        [MigrationConflictKind.InterimSiteDetailsDiffer] = s_interimSiteDetails,
        [MigrationConflictKind.RegistrationNumberDiffers] =
        [
            "registrationReference",
            "previousRegistrationNumbers",
        ],
        [MigrationConflictKind.SeededDetailsDiffer] =
        [
            "companyRegisteredAddress",
            "companyRegisterAddressPostcode",
            "isUkRegisteredAddress",
            "submitterContactDetails",
            "siteAddress",
            "permitNumbers",
        ],
    };

    public static bool Explains(IReadOnlyCollection<MigrationConflictKind> kinds, string path) =>
        kinds.Any(kind =>
            s_fields.TryGetValue(kind, out var fields)
            && Array.Exists(
                fields,
                field => path == field || path.StartsWith(field + ".", StringComparison.Ordinal)
            )
        );
}
