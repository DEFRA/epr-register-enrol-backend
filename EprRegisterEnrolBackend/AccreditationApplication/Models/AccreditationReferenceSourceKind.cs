namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

// RA-690: which kind of existing reference an accreditation reference is derived from (see
// AccreditationReferenceDerivation). Derivation never draws from a sequence counter - only
// registration numbers do (RegulatoryNumberGenerator).
public enum AccreditationReferenceSourceKind
{
    PreviousAccreditation,
    Registration,
}
