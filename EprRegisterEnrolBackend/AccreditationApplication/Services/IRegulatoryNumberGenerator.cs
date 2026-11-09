using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

// RA-448: assembles a Registration number string in the real DEFRA public
// register format R{YY}{AgencyType}{OrgID}{Sequence}{Material}, drawing an
// atomically-incremented sequence value from the correct one of the eight
// registration (R-*) regulatoryNumberSequences pools.
//
// RA-690: registration numbers only - accreditation references are derived from
// the registration reference (AccreditationReferenceDerivation), never drawn
// from a counter, so there is no accreditation code path here.
public interface IRegulatoryNumberGenerator
{
    Task<string> GenerateAsync(RegulatoryNumberSpec spec, CancellationToken ct = default);
}
