using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary><c>DomainModel:DualWrite</c> on: the number belongs to the Registration and is fixed once issued.</summary>
public sealed class RegistrationNumberRules(
    IAccreditationApplicationPersistence applications,
    ApplicationGroupStores stores,
    TimeProvider clock
) : IRegistrationNumberRules
{
    public bool RefusesRegenerate(AccreditationApplicationModel application, string? issued) =>
        (issued ?? application.RegistrationReference) is not null;

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

        // The same bound the migration puts on a registration group.
        var group = await applications.GetRegistrationGroupAsync(
            application.OrganisationId,
            application.RegistrationId,
            ApplicationGroupMigrator.MaxGroupSize,
            session,
            cancellationToken
        );
        foreach (
            var other in group.Where(a =>
                a.Id != application.Id
                && a.ApplicationStatus.IsInFlight()
                && a.RegistrationReference != number
            )
        )
        {
            var update = Builders<AccreditationApplicationModel>
                .Update.Set(a => a.RegistrationReference, number)
                .Set(a => a.DateLastEdited, clock.GetUtcNow().UtcDateTime);
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
