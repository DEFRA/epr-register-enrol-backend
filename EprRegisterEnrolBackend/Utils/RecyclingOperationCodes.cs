using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.Utils;

/// <summary>
/// Single source of truth for overseas-site recycling operation codes (RA-469), shared by
/// <c>PatchRecyclingOperationsRequestValidator</c> (request-shape checks only - it has no access
/// to the application's MaterialType) and the PATCH recycling-operations endpoint in
/// <c>AccreditationApplicationEndpoints.cs</c> (the application/material-type-aware checks).
/// Mirrors epr-register-enrol-frontend's
/// src/server/accreditation/add-overseas-site/recycling-operation-details/controller.js
/// CODES_BY_MATERIAL_TYPE - kept in sync manually, there is no shared package between the repos.
/// </summary>
public static class RecyclingOperationCodes
{
    // The full set of codes an overseas site can carry, regardless of material type - what the
    // request-shape validator checks membership against. CODES_BY_MATERIAL_TYPE below is always
    // a subset of this per material type.
    public static readonly IReadOnlySet<string> AllCodes = new HashSet<string>
    {
        "R3",
        "R4",
        "R5",
        "R12",
        "R13",
    };

    // R12/R13 describe an operation performed in relation to an associated interim site and can
    // never be selected without at least one of R3/R4/R5 (an operation performed at the ORS
    // itself) alongside them - enforced operator-side by requiresAccompanyingCode/
    // CODES_REQUIRING_ACCOMPANIMENT in the frontend controller referenced above.
    public static readonly IReadOnlySet<string> CodesRequiringAccompaniment = new HashSet<string>
    {
        "R12",
        "R13",
    };

    public static readonly IReadOnlyDictionary<
        MaterialType,
        IReadOnlySet<string>
    > CodesByMaterialType = new Dictionary<MaterialType, IReadOnlySet<string>>
    {
        [MaterialType.Aluminium] = new HashSet<string> { "R4", "R12", "R13" },
        [MaterialType.Fibre] = new HashSet<string> { "R3", "R5", "R12", "R13" },
        [MaterialType.Glass] = new HashSet<string> { "R5", "R12", "R13" },
        [MaterialType.Paper] = new HashSet<string> { "R3", "R12", "R13" },
        [MaterialType.Plastic] = new HashSet<string> { "R3", "R12", "R13" },
        [MaterialType.Steel] = new HashSet<string> { "R4", "R12", "R13" },
        [MaterialType.Wood] = new HashSet<string> { "R3", "R12", "R13" },
    };

    // AC10 / requiresAccompanyingCode: true only when R12/R13 is present with nothing else -
    // codes is otherwise assumed non-empty (NotEmpty is the validator's job).
    public static bool RequiresAccompanyingCode(IEnumerable<string> codes)
    {
        var list = codes as ICollection<string> ?? codes.ToList();
        var hasAccompanimentCode = list.Any(CodesRequiringAccompaniment.Contains);
        var hasOtherCode = list.Any(c => !CodesRequiringAccompaniment.Contains(c));
        return hasAccompanimentCode && !hasOtherCode;
    }

    // AC-material-type: codes not offered for the application's material type are rejected.
    // Falls back to AllCodes when materialType has no explicit mapping (shouldn't happen -
    // MaterialType is a closed enum and every value is mapped above - but fails open to "no
    // extra restriction" rather than rejecting every code, mirroring the frontend's own
    // applicableCodesForMaterialType fallback).
    public static IReadOnlySet<string> ApplicableCodesFor(MaterialType materialType) =>
        CodesByMaterialType.TryGetValue(materialType, out var codes) ? codes : AllCodes;
}
