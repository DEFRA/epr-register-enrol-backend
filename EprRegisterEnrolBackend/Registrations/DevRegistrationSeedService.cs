using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DevSeed;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Organisation.Services;

namespace EprRegisterEnrolBackend.Registrations;

// RA-425: local manual testing only. Gives Delta Green's registration (stub org 50003, the RA-690
// DevAccreditationHistorySeedService's organisation) an Approved current version in the domain model, so the
// frontend's material dashboard pages, which show only Approved registrations, have something to show.
// Registered alongside DevAccreditationHistorySeedService, under the same Development-only DevSeedData:Enabled
// flag. The dedicated exporters' (orgs 50018 to 50022) registrations are seeded by DevExporterAccreditationSeedService.
//
// Idempotent: a registration that already has a current version (seeded earlier, written by the seed
// dual-write, or changed by a tester) is left alone.
public class DevRegistrationSeedService(
    RegistrationVersionStore store,
    ILogger<DevRegistrationSeedService> logger
) : DevSeedService(logger)
{
    private const int DeltaGreenOrgId = 50003;

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
        RegistrationVersionDevSeed.ApprovedRegistration(
            DeltaGreenOrgId,
            FakeOrganisationPersistence.Reg50003,
            DevAccreditationHistorySeedService.DeltaGreen2026RegistrationReference,
            WasteProcessingKind.Reprocessor,
            MaterialType.Plastic
        );
}
