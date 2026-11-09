using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary>
/// How the registration-number write path treats a registration's number (B8e). With <c>DomainModel:DualWrite</c>
/// on, a number belongs to the Registration and is fixed once issued: it is never regenerated, an application
/// without one takes the Registration's, and it is copied to every in-flight application of the registration. With
/// the flag off, the legacy per-application rules apply unchanged.
/// </summary>
public interface IRegistrationNumberRules
{
    /// <summary>
    /// Whether a regenerate request must be refused: the number, <paramref name="issued"/> to the Registration or
    /// already on the application, is fixed.
    /// </summary>
    bool RefusesRegenerate(AccreditationApplicationModel application, string? issued);

    /// <summary>The number the application's Registration already holds, or null when it has none.</summary>
    Task<string?> IssuedNumberAsync(
        AccreditationApplicationModel application,
        IClientSessionHandle? session,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Gives <paramref name="number"/> to every other in-flight application of the registration, keeping any
    /// different number one held in its previous numbers.
    /// </summary>
    Task CopyToInFlightApplicationsAsync(
        AccreditationApplicationModel application,
        string number,
        IClientSessionHandle? session,
        CancellationToken cancellationToken = default
    );
}
