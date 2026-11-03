using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Organisation.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Registrations;

// RA-425: local manual testing only. Gives the dev-seeded registrations an Approved current version in the
// domain model, so the frontend's material dashboard pages, which show only Approved registrations, have
// something to show. Registered alongside the RA-690 DevAccreditationHistorySeedService, under the same
// Development-only DevSeedData:Enabled flag.
//
// Idempotent: a registration that already has a current version (seeded earlier, written by the seed
// dual-write, or changed by a tester) is left alone.
public class DevRegistrationSeedService(
    RegistrationVersionStore store,
    ILogger<DevRegistrationSeedService> logger
) : BackgroundService
{
    // Fixed so the seeded versions are recognisable. Nothing reads the Organisation itself yet.
    internal static readonly ObjectId DeltaGreenOrganisationEntityId = ObjectId.Parse(
        "0d0000000000000000050003"
    );
    internal static readonly ObjectId DeltaGreenRegistrationEntityId = ObjectId.Parse(
        "0e0000000000000000050003"
    );

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is never held up by anything below.
        await Task.Yield();
        await SeedAsync(stoppingToken);
    }

    // Extracted so tests can await it deterministically.
    internal async Task SeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var registration in SeededRegistrations())
            {
                await SeedOneAsync(registration, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down before seeding finished - nothing to do.
        }
        catch (Exception ex)
        {
            // Best-effort dev data: a Mongo hiccup must not crash-loop the local backend.
            logger.LogError(ex, "DevRegistrationSeedService: seeding failed");
        }
    }

    private async Task SeedOneAsync(
        RegistrationEntity registration,
        CancellationToken cancellationToken
    )
    {
        var existing = await store.Versions.FindCurrentAsync(
            Builders<RegistrationEntity>.Filter.Eq(
                x => x.ReExRegistrationId,
                registration.ReExRegistrationId
            ),
            cancellationToken: cancellationToken
        );
        if (existing is not null)
        {
            logger.LogInformation(
                "DevRegistrationSeedService: registration {ReExRegistrationId} already present",
                registration.ReExRegistrationId
            );
            return;
        }

        await store.Versions.InsertAsync(
            registration,
            new ChangeInfo { Reason = ChangeReason.ReExImport, ChangedBy = "dev-seed" },
            cancellationToken: cancellationToken
        );
        logger.LogInformation(
            "DevRegistrationSeedService: seeded {Status} registration {ReExRegistrationId}",
            registration.Status,
            registration.ReExRegistrationId
        );
    }

    internal static IReadOnlyList<RegistrationEntity> SeededRegistrations() =>
        [
            new()
            {
                EntityId = DeltaGreenRegistrationEntityId,
                OrganisationId = DeltaGreenOrganisationEntityId,
                ReExRegistrationId = FakeOrganisationPersistence.Reg50003.ToString(),
                RegistrationNumber =
                    DevAccreditationHistorySeedService.DeltaGreen2026RegistrationReference,
                Status = RegistrationStatus.Approved,
                WasteProcessingType = WasteProcessingKind.Reprocessor,
                Material = MaterialType.Plastic,
                Nation = Nation.England,
                Source = new EntitySource { System = SourceSystem.ReEx },
            },
        ];
}
