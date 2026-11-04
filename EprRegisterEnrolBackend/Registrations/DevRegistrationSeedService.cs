using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DevSeed;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Organisation.Services;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Registrations;

// RA-425: local manual testing only. Gives Delta Green's registration (stub org 50003, the RA-690
// DevAccreditationHistorySeedService's organisation) an Approved current version in the domain model, so the
// frontend's material dashboard pages, which show only Approved registrations, have something to show.
// Registered alongside DevAccreditationHistorySeedService, under the same Development-only DevSeedData:Enabled
// flag. The dedicated exporter's (org 50018) registration is seeded by DevExporterAccreditationSeedService.
//
// Idempotent: a registration that already has a current version (seeded earlier, written by the seed
// dual-write, or changed by a tester) is left alone.
public class DevRegistrationSeedService(
    RegistrationVersionStore store,
    ILogger<DevRegistrationSeedService> logger
) : DevSeedService(logger)
{
    // Fixed so the seeded version is recognisable. Nothing reads the Organisation itself yet.
    internal static readonly ObjectId DeltaGreenOrganisationEntityId = ObjectId.Parse(
        "0d0000000000000000050003"
    );
    internal static readonly ObjectId DeltaGreenRegistrationEntityId = ObjectId.Parse(
        "0e0000000000000000050003"
    );

    protected override async Task SeedCoreAsync(CancellationToken cancellationToken)
    {
        var registration = DeltaGreenRegistration();
        if (!await store.SeedIfAbsentAsync(registration, cancellationToken))
        {
            Logger.LogInformation(
                "DevRegistrationSeedService: registration {ReExRegistrationId} already present",
                registration.ReExRegistrationId
            );
            return;
        }

        Logger.LogInformation(
            "DevRegistrationSeedService: seeded {Status} registration {ReExRegistrationId}",
            registration.Status,
            registration.ReExRegistrationId
        );
    }

    internal static RegistrationEntity DeltaGreenRegistration() =>
        new()
        {
            EntityId = DeltaGreenRegistrationEntityId,
            OrganisationId = DeltaGreenOrganisationEntityId,
            ReExRegistrationId = FakeOrganisationPersistence.Reg50003.ToString(),
            RegistrationNumber = DevAccreditationHistorySeedService.DeltaGreen2026RegistrationReference,
            Status = RegistrationStatus.Approved,
            WasteProcessingType = WasteProcessingKind.Reprocessor,
            Material = MaterialType.Plastic,
            Nation = Nation.England,
            Source = new EntitySource { System = SourceSystem.ReEx },
        };
}
