namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

// RA-690: which kind of existing reference an accreditation reference is derived from (see
// AccreditationReferenceDerivation). Derivation never draws from a sequence counter - only
// registration numbers do (RegulatoryNumberGenerator).
public enum AccreditationReferenceSourceKind
{
    PreviousAccreditation,
    Registration,

    // RA-763: last year's accreditation number, kept from ReEx at Seed. An 'A' reference like
    // PreviousAccreditation, but a kind of its own so the issued log says which source was used.
    LastYearsAccreditationNumber,
}