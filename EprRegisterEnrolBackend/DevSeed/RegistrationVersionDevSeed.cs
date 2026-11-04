using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Registrations;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DevSeed;

internal static class RegistrationVersionDevSeed
{
    /// <summary>
    /// An Approved English registration, as imported from ReEx, for the stub ReEx registry's org
    /// <paramref name="orgId"/>.
    /// </summary>
    internal static RegistrationEntity ApprovedRegistration(
        int orgId,
        ObjectId reExRegistrationId,
        string registrationNumber,
        WasteProcessingKind wasteProcessingType,
        MaterialType material
    ) =>
        new()
        {
            // Fixed, from the org id, so the seeded version is recognisable. Nothing reads the
            // Organisation itself yet.
            EntityId = ObjectId.Parse($"0e{orgId:D22}"),
            OrganisationId = ObjectId.Parse($"0d{orgId:D22}"),
            ReExRegistrationId = reExRegistrationId.ToString(),
            RegistrationNumber = registrationNumber,
            Status = RegistrationStatus.Approved,
            WasteProcessingType = wasteProcessingType,
            Material = material,
            Nation = Nation.England,
            Source = new EntitySource { System = SourceSystem.ReEx },
        };

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
