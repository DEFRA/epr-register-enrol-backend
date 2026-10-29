using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
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
    /// <summary>True when an issued number is fixed (DualWrite on).</summary>
    bool NumbersAreFixed { get; }

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

/// <summary><c>DomainModel:DualWrite</c> off: the legacy rules, number by number per application.</summary>
public sealed class LegacyRegistrationNumberRules : IRegistrationNumberRules
{
    public bool NumbersAreFixed => false;

    public Task<string?> IssuedNumberAsync(
        AccreditationApplicationModel application,
        IClientSessionHandle? session,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<string?>(null);

    public Task CopyToInFlightApplicationsAsync(
        AccreditationApplicationModel application,
        string number,
        IClientSessionHandle? session,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;
}

public sealed class RegistrationNumberRules(
    IAccreditationApplicationPersistence applications,
    ApplicationGroupStores stores
) : IRegistrationNumberRules
{
    // The same bound the migration puts on a registration group.
    private const int GroupLimit = 100;

    public bool NumbersAreFixed => true;

    public async Task<string?> IssuedNumberAsync(
        AccreditationApplicationModel application,
        IClientSessionHandle? session,
        CancellationToken cancellationToken = default
    )
    {
        if (application.RegistrationEntityId is not { } registrationId)
        {
            return null;
        }

        var registration = await stores.Registrations.Versions.FindCurrentAsync(
            Builders<RegistrationEntity>.Filter.Eq(r => r.EntityId, registrationId),
            session,
            cancellationToken
        );
        return string.IsNullOrWhiteSpace(registration?.RegistrationNumber)
            ? null
            : registration.RegistrationNumber;
    }

    public async Task CopyToInFlightApplicationsAsync(
        AccreditationApplicationModel application,
        string number,
        IClientSessionHandle? session,
        CancellationToken cancellationToken = default
    )
    {
        if (session is null || string.IsNullOrEmpty(application.RegistrationId))
        {
            return;
        }

        var group = await applications.GetRegistrationGroupAsync(
            application.OrganisationId,
            application.RegistrationId,
            GroupLimit,
            session,
            cancellationToken
        );
        foreach (
            var other in group.Where(a =>
                a.Id != application.Id
                && ApplicationGroupMigrator.InFlight(a)
                && a.RegistrationReference != number
            )
        )
        {
            var update = Builders<AccreditationApplicationModel>
                .Update.Set(a => a.RegistrationReference, number)
                .Set(a => a.DateLastEdited, DateTime.UtcNow);
            if (other.RegistrationReference is { } previous)
            {
                update = update.Push(a => a.PreviousRegistrationNumbers, previous);
            }

            await applications.UpdateFieldsAsync(
                other.Id!.Value,
                null,
                update,
                cancellationToken,
                session
            );
        }
    }
}
