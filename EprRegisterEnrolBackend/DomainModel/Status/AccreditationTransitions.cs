using EprRegisterEnrolBackend.DomainModel.Entities;

namespace EprRegisterEnrolBackend.DomainModel.Status;

/// <summary>
/// The Accreditation lifecycle (design §3.4, Q3): ReEx's <c>VALID_ACC_TRANSITIONS</c> (epr-backend
/// <c>src/domain/organisations/status.js</c>, ADR 0042) without <c>created</c> and <c>rejected</c>, which belong
/// to the Application here. Cancelled is reached only via suspended. The registration-cancellation cascade
/// (approved to cancelled) deliberately bypasses this table, as it does in ReEx.
/// </summary>
public static class AccreditationTransitions
{
    public static StatusTransitions<AccreditationStatus> Status { get; } =
        new(
            "accreditation",
            new Dictionary<AccreditationStatus, AccreditationStatus[]>
            {
                [AccreditationStatus.Approved] = [AccreditationStatus.Suspended],
                [AccreditationStatus.Suspended] =
                [
                    AccreditationStatus.Approved,
                    AccreditationStatus.Cancelled,
                ],
                [AccreditationStatus.Cancelled] = [AccreditationStatus.Approved],
            }
        );
}
