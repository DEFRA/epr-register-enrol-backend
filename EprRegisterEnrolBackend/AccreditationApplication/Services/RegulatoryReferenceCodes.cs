using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// The agency letter, process type letter and material code segments of a regulatory reference
/// (<c>{R|A}</c> + <c>YY</c> + agency + <c>{R|X}</c> + OrgId + sequence + material code).
/// Shared by <see cref="RegulatoryNumberGenerator"/> and the stub ReEx adapter's derived
/// registration reference, so both always produce the same codes.
/// </summary>
public static class RegulatoryReferenceCodes
{
    public static char AgencyLetterFor(Nation nation) =>
        nation switch
        {
            Nation.England => 'E',
            Nation.Scotland => 'S',
            Nation.Wales => 'W',
            Nation.NorthernIreland => 'N',
            _ => throw new ArgumentOutOfRangeException(
                nameof(nation),
                nation,
                "Unrecognised nation."
            ),
        };

    public static char ProcessTypeLetterFor(bool isExporter) => isExporter ? 'X' : 'R';

    // Six of the seven MaterialType values map 1:1. Glass is the exception - it
    // splits into two number-format codes (GO/GR) depending on
    // GlassRecyclingProcess, which is why that parameter must be supplied
    // whenever material is Glass (see RA-448 design notes: GR = Remelt, GO = Other,
    // matching GlassRecyclingProcess's own "glass_re_melt"/"glass_other" naming).
    public static string MaterialCodeFor(
        MaterialType material,
        GlassRecyclingProcess? glassRecyclingProcess
    ) =>
        material switch
        {
            MaterialType.Steel => "ST",
            MaterialType.Wood => "WO",
            MaterialType.Aluminium => "AL",
            MaterialType.Fibre => "FB",
            MaterialType.Paper => "PA",
            MaterialType.Plastic => "PL",
            MaterialType.Glass => glassRecyclingProcess switch
            {
                GlassRecyclingProcess.Remelt => "GR",
                GlassRecyclingProcess.Other => "GO",
                _ => throw new ArgumentException(
                    "GlassRecyclingProcess is required when material is Glass.",
                    nameof(glassRecyclingProcess)
                ),
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(material),
                material,
                "Unrecognised material type."
            ),
        };
}
