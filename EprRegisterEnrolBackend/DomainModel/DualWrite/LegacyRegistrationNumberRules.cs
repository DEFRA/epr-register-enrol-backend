using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary><c>DomainModel:DualWrite</c> off: the legacy rules, number by number per application.</summary>
public sealed class LegacyRegistrationNumberRules : IRegistrationNumberRules
{
    public bool RefusesRegenerate(AccreditationApplicationModel application, string? issued) =>
        false;

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
