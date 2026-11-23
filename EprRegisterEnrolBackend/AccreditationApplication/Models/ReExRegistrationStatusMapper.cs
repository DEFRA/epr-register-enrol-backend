using EprRegisterEnrolBackend.DomainModel.Entities;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

/// <summary>
/// RA-806: maps a ReEx registration's <c>status</c> to a <see cref="RegistrationStatus"/>. The values are ReEx's own
/// registration lifecycle (<c>epr-backend</c> <c>REGISTRATION_STATUS</c>: created, approved, rejected, cancelled),
/// which the domain model adopts (design doc §3.4, Q4). A registration cannot be suspended (PAE-1705), so ReEx sends
/// no <c>suspended</c>. Matched by name, ignoring case; never by number.
/// </summary>
public static class ReExRegistrationStatusMapper
{
    private static readonly Dictionary<string, RegistrationStatus> NameToStatus = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["created"] = RegistrationStatus.Created,
        ["approved"] = RegistrationStatus.Approved,
        ["rejected"] = RegistrationStatus.Rejected,
        ["cancelled"] = RegistrationStatus.Cancelled,
    };

    /// <summary>
    /// The status <paramref name="reExStatus"/> names, or null when it is missing or names none of ReEx's
    /// registration statuses. The caller logs that at Error: there is no default.
    /// </summary>
    public static RegistrationStatus? Map(string? reExStatus) =>
        reExStatus is not null && NameToStatus.TryGetValue(reExStatus, out var status)
            ? status
            : null;
}
