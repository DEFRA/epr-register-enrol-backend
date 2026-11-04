using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Registrations;

namespace EprRegisterEnrolBackend.DevSeed;

internal static class RegistrationVersionDevSeed
{
    /// <summary>
    /// Inserts <paramref name="registration"/> as a new registration unless its ReEx registration already has a
    /// current version (seeded earlier, written by the seed dual-write, or changed by a tester), which is left
    /// alone. Returns whether it inserted.
    /// </summary>
    internal static async Task<bool> SeedIfAbsentAsync(
        this RegistrationVersionStore store,
        RegistrationEntity registration,
        CancellationToken cancellationToken
    )
    {
        var existing = await store.FindCurrentByReExRegistrationIdAsync(
            registration.ReExRegistrationId
                ?? throw new ArgumentException("A seeded registration needs its ReEx id."),
            cancellationToken
        );
        if (existing is not null)
        {
            return false;
        }

        await store.Versions.InsertAsync(
            registration,
            new ChangeInfo { Reason = ChangeReason.ReExImport, ChangedBy = "dev-seed" },
            cancellationToken: cancellationToken
        );
        return true;
    }
}
