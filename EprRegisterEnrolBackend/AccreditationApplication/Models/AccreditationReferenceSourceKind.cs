namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

// RA-690: which existing reference an issued accreditation reference is built on, as logged. A
// continuous renewal names the accreditation it renews (PreviousAccreditation or
// LastYearsAccreditationNumber); a new number names the Registration whose agency, process type
// and OrgId it keeps (RA-783: its sequence is random). Never a sequence counter - only
// registration numbers draw from one (RegulatoryNumberGenerator).
public enum AccreditationReferenceSourceKind
{
    PreviousAccreditation,
    Registration,

    // RA-763: last year's accreditation number, kept from ReEx at Seed. An 'A' reference like
    // PreviousAccreditation, but a kind of its own so the issued log says which source was used.
    LastYearsAccreditationNumber,
}