using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

// RA-448: assembles a Registration or Accreditation number string in the real
// DEFRA public register format {R|A}{YY}{AgencyType}{OrgID}{Sequence}{Material},
// drawing an atomically-incremented sequence value from the correct one of the
// 16 regulatoryNumberSequences pools. One shared method for both number types -
// the only difference is the prefix and which pool is incremented.
public interface IRegulatoryNumberGenerator
{
    Task<string> GenerateAsync(
        NumberType type,
        Nation nation,
        bool isExporter,
        int orgId,
        MaterialType material,
        GlassRecyclingProcess? glassRecyclingProcess,
        int year,
        CancellationToken ct = default
    );
}
