namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

// RA-690: which kind of existing reference an accreditation reference is derived from (see
// AccreditationReferenceDerivation). Deliberately separate from NumberType, which only selects
// a counter pool inside RegulatoryNumberGenerator - derivation never draws from a counter.
public enum AccreditationReferenceSourceKind
{
    PreviousAccreditation,
    Registration,
}
