using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.ReEx;

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
    /// The status <paramref name="reExStatus"/> names. ReEx masters it, so there is no default: a missing status, or
    /// one that is not a ReEx registration status, is logged at Error and fails as bad ReEx data, as the adapters'
    /// other data-integrity failures do (<see cref="ReExErrorKind.ClientError"/>, 500), which fails the seed.
    /// Success carries 200, the status of the ReEx response the value was read from.
    /// </summary>
    public static ReExResult<RegistrationStatus> Map(
        string? reExStatus,
        string organisationId,
        string? registrationId,
        ILogger logger
    )
    {
        if (reExStatus is not null && NameToStatus.TryGetValue(reExStatus, out var status))
        {
            return ReExResult<RegistrationStatus>.Success(status, 200);
        }

        logger.LogError(
            "ReEx registration status is missing or unrecognised for org={OrganisationId} reg={RegistrationId}: {RawStatus}",
            organisationId,
            registrationId,
            reExStatus
        );
        return ReExResult<RegistrationStatus>.Fail(
            new ReExError(
                ReExErrorKind.ClientError,
                "Registration status is missing or is not a ReEx registration status"
            ),
            500
        );
    }
}
