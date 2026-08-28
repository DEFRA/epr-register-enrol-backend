using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.Utils;

/// <summary>
/// Single source of truth for overseas-site recycling operation codes (RA-469, reworked by
/// RA-486), shared by <c>PatchRecyclingOperationsRequestValidator</c>/
/// <c>AddOverseasSiteRequestValidator</c>/<c>PromoteOverseasSiteRequestValidator</c>/
/// <c>AddInterimSiteRequestValidator</c> (request-shape checks only - none of them has access to
/// the application's MaterialType) and the PATCH recycling-operations endpoint in
/// <c>AccreditationApplicationEndpoints.cs</c> (the application/material-type-aware checks, ORS
/// only - an interim site's OperationCodes are never checked against MaterialType, see
/// HasMandatoryInterimCode below). Mirrors epr-register-enrol-frontend's
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

    // RA-486: R12/R13 describe an operation performed at an interim site, R3/R4/R5 an operation
    // performed at the ORS itself. The two are independent designations, not a required pairing -
    // an ORS's mandatory codes are the material ones (HasMandatoryOrsCode below); an interim
    // site's mandatory codes are these (HasMandatoryInterimCode below). Kept as distinct sets
    // (rather than inferring one from AllCodes minus the other) so a future third designation
    // doesn't silently fall into the wrong bucket.
    public static readonly IReadOnlySet<string> MaterialCodes = new HashSet<string>
    {
        "R3",
        "R4",
        "R5",
    };

    public static readonly IReadOnlySet<string> InterimCodes = new HashSet<string> { "R12", "R13" };

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

    // RA-486 AC: an ORS's OperationCodes must include at least one material code - R3, R4 or R5.
    // R12/R13 are optional on the ORS. Replaces the old "R12/R13 can't be selected in isolation"
    // rule with an equivalent-in-effect but explicit mandatory-code rule now that R12/R13 no
    // longer implies an interim site is attached.
    public static bool HasMandatoryOrsCode(IEnumerable<string> codes) =>
        codes.Any(MaterialCodes.Contains);

    // RA-486 AC: an interim site's OperationCodes must include at least one of R12/R13; R3/R4/R5
    // are optional on the interim site (material type is inherited from the parent ORS, not
    // re-validated here).
    public static bool HasMandatoryInterimCode(IEnumerable<string> codes) =>
        codes.Any(InterimCodes.Contains);

    // AC-material-type: codes not offered for the application's material type are rejected.
    // Falls back to AllCodes when materialType has no explicit mapping (shouldn't happen -
    // MaterialType is a closed enum and every value is mapped above - but fails open to "no
    // extra restriction" rather than rejecting every code, mirroring the frontend's own
    // applicableCodesForMaterialType fallback).
    public static IReadOnlySet<string> ApplicableCodesFor(MaterialType materialType) =>
        CodesByMaterialType.TryGetValue(materialType, out var codes) ? codes : AllCodes;
}
