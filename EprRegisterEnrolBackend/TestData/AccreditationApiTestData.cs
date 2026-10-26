using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.TestData;

/// <summary>
/// RA-646: fixed accreditations that other teams can point their services at in the test
/// environments (dev, test, ext-test) and read through the accreditation API. The same
/// definitions drive our own setup and teardown, so a test can put the database into this known
/// state and clear it again.
/// </summary>
public static class AccreditationApiTestData
{
    public const int Year = 2027;

    // Organisation numbers are in a reserved 99xxxx range so the records are obviously test data
    // and can never collide with a real organisation.
    public static readonly TestScenario ExporterOneOrs = new(
        Name: "exporter-1-ors",
        Id: ObjectId.Parse("7e57da7a0000000000000001"),
        OrgId: 990001,
        Material: MaterialType.Paper,
        MaterialCode: "PA",
        IsExporter: true,
        OverseasSites: 1,
        InterimSitesPerSite: 1
    );

    public static readonly TestScenario ExporterTenOrs = new(
        Name: "exporter-10-ors",
        Id: ObjectId.Parse("7e57da7a0000000000000002"),
        OrgId: 990002,
        Material: MaterialType.Fibre,
        MaterialCode: "FB",
        IsExporter: true,
        OverseasSites: 10,
        InterimSitesPerSite: 2
    );

    public static readonly TestScenario ReprocessorPlastic = new(
        Name: "reprocessor-plastic",
        Id: ObjectId.Parse("7e57da7a0000000000000003"),
        OrgId: 990003,
        Material: MaterialType.Plastic,
        MaterialCode: "PL",
        IsExporter: false,
        OverseasSites: 0,
        InterimSitesPerSite: 0
    );

    // "Metal": the application's material types include steel and aluminium; steel is used here.
    public static readonly TestScenario ReprocessorMetal = new(
        Name: "reprocessor-metal",
        Id: ObjectId.Parse("7e57da7a0000000000000004"),
        OrgId: 990004,
        Material: MaterialType.Steel,
        MaterialCode: "ST",
        IsExporter: false,
        OverseasSites: 0,
        InterimSitesPerSite: 0
    );

    public static readonly IReadOnlyList<TestScenario> All =
    [
        ExporterOneOrs,
        ExporterTenOrs,
        ReprocessorPlastic,
        ReprocessorMetal,
    ];
}

public sealed record TestScenario(
    string Name,
    ObjectId Id,
    int OrgId,
    MaterialType Material,
    string MaterialCode,
    bool IsExporter,
    int OverseasSites,
    int InterimSitesPerSite
)
{
    private string Kind => IsExporter ? "EX" : "ER";

    private string Tail => $"{OrgId}{(IsExporter ? "0002" : "0000")}{MaterialCode}";

    public string RegistrationReference => $"R26{Kind}{Tail}";

    public string AccreditationReference => $"A27{Kind}{Tail}";

    public string OrganisationName => $"Test Data {Name}";

    /// <summary>Builds the approved application. Every value is fixed, so it is the same each time.</summary>
    public AccreditationApplicationModel Build()
    {
        var fixedDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var sites = Enumerable.Range(1, OverseasSites).Select(BuildOverseasSite).ToList();

        return new AccreditationApplicationModel
        {
            Id = Id,
            OrganisationId = OrgId.ToString(),
            OrgId = OrgId,
            OrganisationName = OrganisationName,
            Year = AccreditationApiTestData.Year,
            RegistrationId = $"7e57da7a{OrgId:D16}",
            RegistrationReference = RegistrationReference,
            AccreditationReference = AccreditationReference,
            ApplicationStatus = ApplicationStatus.Approved,
            MaterialType = Material,
            IsExporter = IsExporter,
            WasteProcessingType = IsExporter ? "exporter" : "reprocessor",
            Nation = Nation.England,
            SiteAddress = IsExporter ? null : "1 Test Data Road, Testford, TE1 1ST",
            CompanyRegisterAddressPostcode = "TE1 1ST",
            CompanyRegisteredAddress = "1 Test Data Road, Testford, TE1 1ST",
            IsUkRegisteredAddress = true,
            CompaniesHouseNumber = $"{OrgId:D8}",
            SubmitterContactDetails = new SubmitterContactDetailsModel
            {
                FullName = "Test Data Submitter",
                Email = "test.data@example.com",
                Phone = "01234 567890",
                JobTitle = "Test Data Contact",
            },
            Prns = new AccreditationApplicationPrns
            {
                PlannedTonnageBand = PlannedTonnageBand.UpTo5000,
                Authorisers =
                [
                    new PrnsAuthoriser
                    {
                        FullName = "Test Data Authoriser",
                        Email = "test.authoriser@example.com",
                    },
                ],
                SectionStatus = SectionStatus.Completed,
            },
            BusinessPlan = new AccreditationApplicationBusinessPlan
            {
                NewInfrastructurePercent = 20,
                PriceSupportPercent = 20,
                BusinessCollectionsPercent = 15,
                CommunicationsPercent = 15,
                NewMarketsPercent = 10,
                NewUsesPercent = 10,
                OtherPercent = 10,
                NewInfrastructureDetail = "Test data: new infrastructure",
                PriceSupportDetail = "Test data: price support",
                BusinessCollectionsDetail = "Test data: business collections",
                CommunicationsDetail = "Test data: communications",
                NewMarketsDetail = "Test data: new markets",
                NewUsesDetail = "Test data: new uses",
                OtherDetail = "Test data: other",
                SectionStatus = SectionStatus.Completed,
            },
            OverseasSites = IsExporter
                ? new AccreditationApplicationOverseasSites
                {
                    Sites = sites,
                    SectionStatus = SectionStatus.Completed,
                }
                : null,
            CreatedAt = fixedDate,
            UpdatedAt = fixedDate,
            DateLastEdited = fixedDate,
        };
    }

    private OverseasSiteModel BuildOverseasSite(int number)
    {
        var orsId = number.ToString("D3");
        var siteId = (OrgId * 100) + number;
        var interim = Enumerable
            .Range(1, InterimSitesPerSite)
            .Select(i => new InterimSiteModel
            {
                SiteId = (siteId * 10) + i,
                SiteNumber = $"TD-{OrgId}-{orsId}-{i}",
                Country = "Netherlands",
                SiteName = $"Test Data Interim Site {orsId}-{i}",
                AddressLine1 = $"{i} Tussenstraat",
                TownOrCity = "Rotterdam",
                Postcode = "3011 AA",
                ContactName = "Test Data Interim Contact",
                ContactEmail = "test.interim@example.com",
                ContactPhone = "+31 10 000 0000",
                OperationCodes = ["R12"],
                CreatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            })
            .ToList();

        var site = new OverseasSiteModel
        {
            SiteId = siteId,
            OrsId = orsId,
            SiteName = $"Test Data Overseas Site {orsId}",
            SiteAddress = $"{number} Hafenstrasse, Hamburg, Germany",
            AddressLine1 = $"{number} Hafenstrasse",
            TownOrCity = "Hamburg",
            Country = "Germany",
            OperationCodes = ["R3"],
            IsEu = true,
            IsOecd = true,
            ValidFrom = "2027-01-01T00:00:00.000Z",
            Selected = true,
            ContactName = "Test Data Site Contact",
            ContactEmail = "test.site@example.com",
            ContactPhone = "+49 40 000000",
            InterimSites = interim,
        };
        InterimSiteSync.SyncMirror(site);
        return site;
    }
}

public class TestDataConfig
{
    public const string SectionName = "TestData";

    /// <summary>
    /// Seeds the fixed test accreditations at startup. Off by default; switch it on only in the
    /// test environments (dev, test, ext-test), never in production.
    /// </summary>
    public bool SeedOnStartup { get; set; }
}

/// <summary>
/// Puts the database into the known test state and clears it again. Internal: nothing here is
/// exposed over HTTP. Seeding is an upsert by fixed id, so it is safe to run repeatedly, and
/// teardown removes only the fixed test records, never anything else.
/// </summary>
public class AccreditationApiTestDataSeeder(
    IAccreditationApplicationPersistence persistence,
    ILogger<AccreditationApiTestDataSeeder> logger
)
{
    public async Task SeedAsync()
    {
        foreach (var scenario in AccreditationApiTestData.All)
            await persistence.UpsertAsync(scenario.Build());

        logger.LogInformation(
            "Seeded {Count} test accreditations (organisations {Organisations})",
            AccreditationApiTestData.All.Count,
            string.Join(", ", AccreditationApiTestData.All.Select(s => s.OrgId))
        );
    }

    public async Task<long> TeardownAsync()
    {
        var removed = await persistence.DeleteManyAsync(
            AccreditationApiTestData.All.Select(s => s.Id)
        );
        logger.LogInformation("Removed {Count} test accreditations", removed);
        return removed;
    }

    /// <summary>Clear back to the known state: remove the test records, then seed them afresh.</summary>
    public async Task ResetAsync()
    {
        await TeardownAsync();
        await SeedAsync();
    }
}

/// <summary>Seeds the test accreditations in the background at startup when switched on.</summary>
public class AccreditationApiTestDataSeedingService(
    IServiceProvider serviceProvider,
    IOptions<TestDataConfig> config,
    ILogger<AccreditationApiTestDataSeedingService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.Value.SeedOnStartup)
            return;

        // Yield so host startup is never held up; a Mongo hiccup logs and is retried on the
        // next deploy rather than crash-looping the backend.
        await Task.Yield();
        try
        {
            await serviceProvider
                .GetRequiredService<AccreditationApiTestDataSeeder>()
                .SeedAsync()
                .WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Seeding the test accreditations failed");
        }
    }
}
