namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

// RA-448: which of the 16 regulatoryNumberSequences pools a generated number
// draws from - never serialized on the wire, purely an internal generator
// parameter (the two endpoints imply it by which route was called).
public enum NumberType
{
    Registration,
    Accreditation,
}
