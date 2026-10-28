using System.Net;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.Test.Auth;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.TestData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.TestData;

// The seeder against the in-memory fake: what it puts in, that it is repeatable, and that
// teardown touches only its own records.
public class AccreditationApiTestDataSeederTests
{
    private readonly FakeAccreditationApplicationPersistence _persistence = new();
    private readonly AccreditationApiTestDataSeeder _seeder;

    public AccreditationApiTestDataSeederTests() =>
        _seeder = new AccreditationApiTestDataSeeder(
            _persistence,
            NullLogger<AccreditationApiTestDataSeeder>.Instance
        );

    private static AccreditationApplicationModel Built(TestScenario scenario) => scenario.Build();

    [Fact]
    public void ExporterWithOneOrs_HasOneOrsAndOneInterimSite()
    {
        var sites = Built(AccreditationApiTestData.ExporterOneOrs).OverseasSites!.Sites;

        sites.Should().ContainSingle();
        sites[0].InterimSites.Should().ContainSingle();
    }

    [Fact]
    public void ExporterWithTenOrs_HasTenOrsEachWithTwoInterimSites()
    {
        var sites = Built(AccreditationApiTestData.ExporterTenOrs).OverseasSites!.Sites;

        sites.Should().HaveCount(10);
        sites.Select(s => s.OrsId).Should().Equal(Enumerable.Range(1, 10).Select(i => $"{i:D3}"));
        sites.Should().OnlyContain(s => s.InterimSites.Count == 2);
    }

    [Fact]
    public void Reprocessors_AreForPlasticAndMetal_WithNoOverseasSites()
    {
        var plastic = Built(AccreditationApiTestData.ReprocessorPlastic);
        var metal = Built(AccreditationApiTestData.ReprocessorMetal);

        plastic.MaterialType.Should().Be(MaterialType.Plastic);
        metal.MaterialType.Should().Be(MaterialType.Steel);
        new[] { plastic, metal }.Should().OnlyContain(a => !a.IsExporter && a.OverseasSites == null);
    }

    [Fact]
    public void EveryScenario_IsAnApproved2027Accreditation_ThatTheApiWillServe()
    {
        foreach (var scenario in AccreditationApiTestData.All)
        {
            var application = scenario.Build();
            application.ApplicationStatus.Should().Be(ApplicationStatus.Approved, scenario.Name);
            application.AccreditationReference.Should().NotBeNullOrWhiteSpace(scenario.Name);
            application.RegistrationReference.Should().NotBeNullOrWhiteSpace(scenario.Name);
            application.OrgId.Should().Be(scenario.OrgId, scenario.Name);
            application.Year.Should().Be(2027, scenario.Name);
        }
    }

    [Fact]
    public void Scenarios_HaveDistinctIdsOrganisationsAndReferences()
    {
        var all = AccreditationApiTestData.All;

        all.Select(s => s.Id).Distinct().Should().HaveCount(all.Count);
        all.Select(s => s.OrgId).Distinct().Should().HaveCount(all.Count);
        all.Select(s => s.RegistrationReference).Distinct().Should().HaveCount(all.Count);
        all.Select(s => s.AccreditationReference).Distinct().Should().HaveCount(all.Count);
    }

    [Fact]
    public void Scenarios_AreTheSameEveryTimeTheyAreBuilt()
    {
        foreach (var scenario in AccreditationApiTestData.All)
        {
            JsonSerializer
                .Serialize(scenario.Build())
                .Should()
                .Be(JsonSerializer.Serialize(scenario.Build()), scenario.Name);
        }
    }

    [Fact]
    public async Task Seed_CreatesEveryScenario_AndIsRepeatable()
    {
        await _seeder.SeedAsync();
        await _seeder.SeedAsync();

        foreach (var scenario in AccreditationApiTestData.All)
        {
            var found = await _persistence.GetGrantedAccreditationAsync(
                scenario.OrgId,
                scenario.RegistrationReference,
                2027,
                2027
            );
            found.Should().NotBeNull(scenario.Name);
        }

        var all = await _persistence.GetByOrganisationAsync(
            AccreditationApiTestData.ExporterTenOrs.OrgId.ToString()
        );
        all.Should().ContainSingle();
    }

    [Fact]
    public async Task Teardown_RemovesOnlyTheTestRecords()
    {
        var other = await _persistence.CreateAsync(
            new AccreditationApplicationModel
            {
                OrganisationId = "someone-else",
                OrgId = 500027,
                Year = 2027,
                MaterialType = MaterialType.Glass,
                ApplicationStatus = ApplicationStatus.Approved,
            }
        );
        await _seeder.SeedAsync();

        var removed = await _seeder.TeardownAsync();

        removed.Should().Be(AccreditationApiTestData.All.Count);
        (await _persistence.GetByIdAsync("someone-else", other!.ApplicationId!))
            .Should()
            .NotBeNull();
        (await _persistence.GetByOrganisationAsync("990001")).Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_RestoresTheKnownState_AfterATestChangesIt()
    {
        await _seeder.SeedAsync();
        var scenario = AccreditationApiTestData.ReprocessorPlastic;
        var changed = Built(scenario);
        changed.ApplicationStatus = ApplicationStatus.Withdrawn;
        await _persistence.UpsertAsync(changed);

        await _seeder.ResetAsync();

        (
            await _persistence.GetGrantedAccreditationAsync(
                scenario.OrgId,
                scenario.RegistrationReference,
                2027,
                2027
            )
        )
            .Should()
            .NotBeNull();
    }
}

// The seeded data through the real host, auth and Mongo: what another team's service would get
// when it points at us.
public class AccreditationApiTestDataApiTests : IDisposable
{
    private const string ClientId = "test-consumer";
    private const string Secret = "test-data-integration-secret";

    private readonly EphemeralMongoTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApiTestDataApiTests(MongoIntegrationFixture fixture)
    {
        _factory = new EphemeralMongoTestFactory(
            fixture,
            "td_api",
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["ServiceClients:Clients:0:Name"] = "consumer",
                ["ServiceClients:Clients:0:ClientId"] = ClientId,
                ["AUTH_SHARED_SECRET:CONSUMER"] = Secret,
                ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = "integration-management-be-secret",
                ["AUTH_SHARED_SECRET:FRONTEND"] = "integration-frontend-secret",
                ["CaseWorking:UseStub"] = "true",
            }
        );
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AccreditationApiTestDataSeeder Seeder =>
        _factory.Services.GetRequiredService<AccreditationApiTestDataSeeder>();

    private async Task<JsonElement> GetJson(string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await _client.SendAsync(
            ServiceClientSigner.Request(HttpMethod.Get, path, ClientId, Secret),
            Ct
        );
        response.StatusCode.Should().Be(expected, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private static string AccreditationPath(TestScenario s) =>
        $"/v1/organisations/{s.OrgId}/registrations/{s.RegistrationReference}/accreditations/2027";

    [Fact]
    public async Task EveryScenario_IsServedByTheApi_OnceSeeded()
    {
        await Seeder.SeedAsync();

        foreach (var scenario in AccreditationApiTestData.All)
        {
            var body = await GetJson(AccreditationPath(scenario));
            body.GetProperty("accreditationNumber").GetString().Should().Be(scenario.AccreditationReference);
            body.GetProperty("organisationNumber").GetInt32().Should().Be(scenario.OrgId);
            body.GetProperty("registrationNumber").GetString().Should().Be(scenario.RegistrationReference);
            body.GetProperty("status").GetString().Should().Be("approved");
            body.GetProperty("material").GetString().Should().Be(scenario.Material.ToString());
            body.GetProperty("wasteProcessingType").GetString().Should().Be(scenario.IsExporter ? "exporter" : "reprocessor");
        }
    }

    [Fact]
    public async Task ExporterWithTenOrs_ListsTenSites_EachWithTwoInterimSites()
    {
        await Seeder.SeedAsync();
        var path = AccreditationPath(AccreditationApiTestData.ExporterTenOrs);

        var page = await GetJson($"{path}/overseas-sites?limit=100");

        page.GetProperty("totalItems").GetInt32().Should().Be(10);
        foreach (var site in page.GetProperty("items").EnumerateArray())
            site.GetProperty("interimSites").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task ExporterWithOneOrs_ServesTheSiteWithItsInterimSite()
    {
        await Seeder.SeedAsync();
        var path = AccreditationPath(AccreditationApiTestData.ExporterOneOrs);

        var site = await GetJson($"{path}/overseas-sites/001");

        site.GetProperty("orsId").GetString().Should().Be("001");
        site.GetProperty("interimSites").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Reprocessors_HaveNoOverseasSites()
    {
        await Seeder.SeedAsync();

        foreach (var scenario in new[] { AccreditationApiTestData.ReprocessorPlastic, AccreditationApiTestData.ReprocessorMetal })
        {
            var page = await GetJson($"{AccreditationPath(scenario)}/overseas-sites");
            page.GetProperty("totalItems").GetInt32().Should().Be(0, scenario.Name);
        }
    }

    [Fact]
    public async Task Teardown_MakesTheApiReturn404_AndReset_BringsThemBack()
    {
        await Seeder.SeedAsync();
        var path = AccreditationPath(AccreditationApiTestData.ReprocessorPlastic);

        await Seeder.TeardownAsync();
        await GetJson(path, HttpStatusCode.NotFound);

        await Seeder.ResetAsync();
        await GetJson(path);
    }

    [Fact]
    public async Task NothingIsSeeded_ByDefault()
    {
        await GetJson(
            AccreditationPath(AccreditationApiTestData.ReprocessorPlastic),
            HttpStatusCode.NotFound
        );
    }
}

// Switched on by configuration, the host seeds the data itself at startup.
public class AccreditationApiTestDataStartupTests
{
    [Fact]
    public async Task SeedOnStartup_SeedsTheTestAccreditations()
    {
        var fixture = new MongoIntegrationFixture();
        await fixture.InitializeAsync();
        try
        {
            using var factory = new EphemeralMongoTestFactory(
                fixture,
                "td_start",
                Environments.Production,
                new Dictionary<string, string?>
                {
                    ["TestData:SeedOnStartup"] = "true",
                    ["CaseWorking:UseStub"] = "true",
                }
            );
            _ = factory.CreateClient();
            var persistence = factory.Services.GetRequiredService<IAccreditationApplicationPersistence>();
            var scenario = AccreditationApiTestData.ReprocessorMetal;

            AccreditationApplicationModel? found = null;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (found is null && DateTime.UtcNow < deadline)
            {
                found = await persistence.GetGrantedAccreditationAsync(
                    scenario.OrgId,
                    scenario.RegistrationReference,
                    2027,
                    2027
                );
                if (found is null)
                    await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            found.Should().NotBeNull();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}

// The startup seeding service must never take the host down: a failure is logged, shutdown is
// honoured, and with the setting off it does nothing.
public class AccreditationApiTestDataSeedingServiceTests
{
    private static AccreditationApiTestDataSeedingService Service(
        IAccreditationApplicationPersistence persistence,
        bool seedOnStartup
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(persistence);
        services.AddSingleton(
            new AccreditationApiTestDataSeeder(
                persistence,
                NullLogger<AccreditationApiTestDataSeeder>.Instance
            )
        );
        return new AccreditationApiTestDataSeedingService(
            services.BuildServiceProvider(),
            Options.Create(new TestDataConfig { SeedOnStartup = seedOnStartup }),
            NullLogger<AccreditationApiTestDataSeedingService>.Instance
        );
    }

    [Fact]
    public async Task WhenSwitchedOff_NothingIsSeeded()
    {
        var persistence = Substitute.For<IAccreditationApplicationPersistence>();
        using var service = Service(persistence, seedOnStartup: false);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!;

        await persistence.DidNotReceiveWithAnyArgs().UpsertAsync(default!);
    }

    [Fact]
    public async Task WhenSeedingFails_TheErrorIsLogged_AndTheServiceDoesNotThrow()
    {
        var persistence = Substitute.For<IAccreditationApplicationPersistence>();
        persistence
            .UpsertAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns(Task.FromException(new InvalidOperationException("mongo is down")));
        using var service = Service(persistence, seedOnStartup: true);

        await service.StartAsync(TestContext.Current.CancellationToken);

        await service.ExecuteTask!;
        await persistence.Received(1).UpsertAsync(Arg.Any<AccreditationApplicationModel>());
    }

    [Fact]
    public async Task WhenTheHostStopsMidSeed_TheServiceEndsWithoutThrowing()
    {
        var persistence = Substitute.For<IAccreditationApplicationPersistence>();
        // Signal once the seed is genuinely in flight and then never complete it. Stopping before this point
        // is a different case: the host cancels before seeding starts, and a BackgroundService stopped that
        // early ends Canceled rather than RanToCompletion, which made this test pass or fail on timing alone.
        var seeding = new TaskCompletionSource();
        persistence
            .UpsertAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns(_ =>
            {
                seeding.TrySetResult();
                return new TaskCompletionSource().Task;
            });
        using var service = Service(persistence, seedOnStartup: true);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await seeding.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var ex = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None));
        ex.Should().BeNull(ex?.ToString());
        var finished = await Task.WhenAny(service.ExecuteTask!, Task.Delay(5000, TestContext.Current.CancellationToken));
        finished.Should().BeSameAs(service.ExecuteTask, "the service should end when the host stops");
        service.ExecuteTask!.Status.Should().Be(TaskStatus.RanToCompletion, service.ExecuteTask.Exception?.ToString());
    }
}
