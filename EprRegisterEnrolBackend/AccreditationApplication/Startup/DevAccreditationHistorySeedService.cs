using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Organisation.Services;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-690: local manual testing only. Gives Delta Green Ltd (stub org 50003, Plastic) an approved
// 2026 accreditation held in this backend, so approving its 2027 "Re-Accreditation" application
// exercises the previous-accreditation path (see .tasks/ra-690-testing-guidelines.md in the
// parent repo). Registered only in Development with DevSeedData:Enabled, which the local compose
// stack sets. The test hosts also run as Development, so the flag keeps this out of them.
//
// The accreditation reference's sequence (0042) deliberately differs from the stub's registration
// sequence for this registration (6908), so the two possible sources derive different 2027
// references: A27ER0500030042PL (previous accreditation, correct) and A27ER0500036908PL
// (registration reference).
//
// Idempotent: a fixed id means it is inserted once and left alone after that, including any
// changes a tester makes to it.
public class DevAccreditationHistorySeedService(
    IAccreditationApplicationPersistence persistence,
    ILogger<DevAccreditationHistorySeedService> logger
) : BackgroundService
{
    public const string ConfigKey = "DevSeedData:Enabled";

    internal const string DeltaGreenOrganisationId = "50003";
    internal static readonly ObjectId DeltaGreen2026ApplicationId = ObjectId.Parse(
        "a26000000000000000050003"
    );
    internal const int DeltaGreen2026Year = 2026;

    // What StubReExApiAdapter returns for this registration when asked for 2025, i.e. what Seed
    // would have stored for a 2026 application.
    internal const string DeltaGreen2026RegistrationReference = "R25ER0500036908PL";
    internal const string DeltaGreen2026AccreditationReference = "A26ER0500030042PL";

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
            var existing = await persistence
                .GetByIdAsync(DeltaGreenOrganisationId, DeltaGreen2026ApplicationId.ToString())
                .WaitAsync(cancellationToken);
            if (existing is not null)
            {
                logger.LogInformation(
                    "DevAccreditationHistorySeedService: Delta Green 2026 accreditation already present"
                );
                return;
            }

            var created = await persistence
                .CreateAsync(DeltaGreen2026Accreditation())
                .WaitAsync(cancellationToken);
            if (created is null)
            {
                logger.LogWarning(
                    "DevAccreditationHistorySeedService: failed to seed the Delta Green 2026 accreditation"
                );
                return;
            }

            logger.LogInformation(
                "DevAccreditationHistorySeedService: seeded Delta Green 2026 accreditation {AccreditationReference}",
                DeltaGreen2026AccreditationReference
            );
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down before seeding finished - nothing to do.
        }
        catch (Exception ex)
        {
            // Best-effort dev data: a Mongo hiccup must not crash-loop the local backend.
            logger.LogError(ex, "DevAccreditationHistorySeedService: seeding failed");
        }
    }

    internal static AccreditationApplicationModel DeltaGreen2026Accreditation()
    {
        var approvedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        return new AccreditationApplicationModel
        {
            Id = DeltaGreen2026ApplicationId,
            OrganisationId = DeltaGreenOrganisationId,
            OrgId = 50003,
            OrganisationName = "Delta Green Ltd",
            Year = DeltaGreen2026Year,
            RegistrationId = FakeOrganisationPersistence.Reg50003.ToString(),
            IsExporter = false,
            Nation = Nation.England,
            SiteAddress = "Green Works, Leeds, LS10 1AB",
            CompanyRegisterAddressPostcode = "LS10 1AB",
            CompanyRegisteredAddress = "Green Works, Leeds, LS10 1AB",
            CompaniesHouseNumber = "12345003",
            PermitNumbers = ["WML50003"],
            WasteProcessingType = "reprocessor",
            RegistrationReference = DeltaGreen2026RegistrationReference,
            AccreditationReference = DeltaGreen2026AccreditationReference,
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = ApplicationStatus.Approved,
            SourceYear = DeltaGreen2026Year - 1,
            CreatedAt = approvedAt,
            UpdatedAt = approvedAt,
            DateLastEdited = approvedAt,
        };
    }
}
