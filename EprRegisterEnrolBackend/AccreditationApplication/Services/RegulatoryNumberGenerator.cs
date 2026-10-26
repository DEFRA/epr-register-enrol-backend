using EprRegisterEnrolBackend.AccreditationApplication.Models;
using static EprRegisterEnrolBackend.AccreditationApplication.Services.RegulatoryReferenceCodes;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public class RegulatoryNumberGenerator(IRegulatoryNumberSequenceCounterPersistence counters)
    : IRegulatoryNumberGenerator
{
    public async Task<string> GenerateAsync(
        RegulatoryNumberSpec spec,
        CancellationToken ct = default
    )
    {
        // Everything below is pure (no I/O) and runs before the atomic increment,
        // so an invalid combination throws without ever touching a counter (AC6) -
        // a defence-in-depth backstop even though the calling endpoint validates
        // first too.
        var agencyLetter = AgencyLetterFor(spec.Nation);
        var processTypeLetter = ProcessTypeLetterFor(spec.IsExporter);
        var agencyType = $"{agencyLetter}{processTypeLetter}";
        var materialCode = MaterialCodeFor(spec.Material, spec.GlassRecyclingProcess);
        var prefix = spec.Type == NumberType.Registration ? 'R' : 'A';
        var poolKey = $"{prefix}-{agencyType}";

        var sequence = await counters.IncrementAsync(poolKey, ct);

        return $"{prefix}{spec.Year % 100:D2}{agencyType}{spec.OrgId:D6}{sequence:D4}{materialCode}";
    }
}
