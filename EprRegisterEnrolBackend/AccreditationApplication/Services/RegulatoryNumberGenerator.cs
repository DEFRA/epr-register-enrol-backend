using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public class RegulatoryNumberGenerator(IRegulatoryNumberSequenceCounterPersistence counters)
    : IRegulatoryNumberGenerator
{
    public async Task<string> GenerateAsync(
        NumberType type,
        Nation nation,
        bool isExporter,
        int orgId,
        MaterialType material,
        GlassRecyclingProcess? glassRecyclingProcess,
        int year,
        CancellationToken ct = default
    )
    {
        // Everything below is pure (no I/O) and runs before the atomic increment,
        // so an invalid combination throws without ever touching a counter (AC6) -
        // a defence-in-depth backstop even though the calling endpoint validates
        // first too.
        var agencyLetter = AgencyLetterFor(nation);
        var processTypeLetter = isExporter ? 'X' : 'R';
        var agencyType = $"{agencyLetter}{processTypeLetter}";
        var materialCode = MaterialCodeFor(material, glassRecyclingProcess);
        var prefix = type == NumberType.Registration ? 'R' : 'A';
        var poolKey = $"{prefix}-{agencyType}";

        var sequence = await counters.IncrementAsync(poolKey, ct);

        return $"{prefix}{year % 100:D2}{agencyType}{orgId:D6}{sequence:D4}{materialCode}";
    }

    private static char AgencyLetterFor(Nation nation) =>
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

    // Six of the seven MaterialType values map 1:1. Glass is the exception - it
    // splits into two number-format codes (GO/GR) depending on
    // GlassRecyclingProcess, which is why that parameter must be supplied
    // whenever material is Glass (see RA-448 design notes: GR = Remelt, GO = Other,
    // matching GlassRecyclingProcess's own "glass_re_melt"/"glass_other" naming).
    private static string MaterialCodeFor(
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
