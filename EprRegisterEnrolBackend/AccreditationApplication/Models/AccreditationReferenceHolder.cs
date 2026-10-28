using MongoDB.Bson;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

// RA-690: another application holding an accreditation reference that is about to be issued.
public sealed record AccreditationReferenceHolder(ObjectId Id, ApplicationStatus ApplicationStatus)
{
    // A withdrawn application keeps its reference for audit but never blocks a restart from
    // being issued the same one. Every other status, Approved included, still blocks.
    public bool BlocksIssuing => ApplicationStatus != ApplicationStatus.Withdrawn;
}
