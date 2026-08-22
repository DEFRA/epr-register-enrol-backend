using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

// RA-469 AC15/AC19: write-only audit trail for the recycling-operations PATCH endpoint. No read
// method is exposed - nothing in this backend's scope needs to query it back (a future UI-facing
// read would be a management-be/management-fe concern, not this one).
public interface IRecyclingOperationsAuditPersistence
{
    Task RecordAsync(RecyclingOperationsAuditRecord record, CancellationToken ct = default);
}
