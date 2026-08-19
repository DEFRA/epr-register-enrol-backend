using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

public class RegulatoryNumberEndpointsTests : IClassFixture<AccreditationApplicationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public RegulatoryNumberEndpointsTests(AccreditationApplicationTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private void Reset()
    {
        _factory.FakePersistence.Clear();
        _factory.FakeCounters.Clear();
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCaseWorkingAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCdpUploaderService.ClearSubstitute(ClearOptions.All);
    }

    private AccreditationApplicationModel SeedApplication(
        string orgId = "org-123",
        ApplicationStatus status = ApplicationStatus.Saved,
        Action<AccreditationApplicationModel>? configure = null
    )
    {
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = orgId,
            Year = 2026,
            MaterialType = MaterialType.Wood,
            ApplicationStatus = status,
        };
        configure?.Invoke(app);
        _factory.FakePersistence.Seed(app);
        return app;
    }

    private string RegistrationNumberUrl(AccreditationApplicationModel app) =>
        $"/api/v1/accreditation-applications/{app.OrganisationId}/{app.ApplicationId}/registration-number";

    [Fact]
    public async Task GenerateOrUpdateRegistrationNumber_no_existing_number_generates_and_persists_one()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            RegistrationNumberUrl(app),
            new
            {
                nation = "England",
                orgId = 500027,
                year = 2026,
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions
        );
        body!.RegistrationReference.Should().Be("R26ER5000270001WO");
    }

    [Fact]
    public async Task GenerateOrUpdateRegistrationNumber_idempotent_recall_returns_existing_number_unchanged()
    {
        Reset();
        var app = SeedApplication(configure: a => a.RegistrationReference = "R26ER5000270001WO");

        var response = await _client.PostAsJsonAsync(
            RegistrationNumberUrl(app),
            new { nation = "England", orgId = 999999 }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions
        );
        body!.RegistrationReference.Should().Be("R26ER5000270001WO");
        (await _factory.FakeCounters.GetCurrentMaxAsync("R-ER")).Should().BeNull();
    }

    [Fact]
    public async Task GenerateOrUpdateRegistrationNumber_regenerate_draws_a_new_sequence_and_preserves_the_old_one()
    {
        Reset();
        _factory.FakeCounters.Seed("R-ER", 1);
        var app = SeedApplication(configure: a => a.RegistrationReference = "R26ER5000270001WO");

        var response = await _client.PostAsJsonAsync(
            RegistrationNumberUrl(app),
            new
            {
                nation = "England",
                orgId = 500027,
                year = 2026,
                regenerate = true,
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions
        );
        body!.RegistrationReference.Should().Be("R26ER5000270002WO");
        body.PreviousRegistrationNumbers.Should()
            .ContainSingle()
            .Which.Should()
            .Be("R26ER5000270001WO");
        (await _factory.FakeCounters.GetCurrentMaxAsync("R-ER")).Should().Be(2);
    }

    [Fact]
    public async Task GenerateOrUpdateRegistrationNumber_missing_application_returns_404()
    {
        Reset();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-1/{ObjectId.GenerateNewId()}/registration-number",
            new { nation = "England", orgId = 1 }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task GenerateOrUpdateRegistrationNumber_terminal_status_returns_409_without_touching_the_counter(
        ApplicationStatus status
    )
    {
        Reset();
        var app = SeedApplication(status: status);

        var response = await _client.PostAsJsonAsync(
            RegistrationNumberUrl(app),
            new { nation = "England", orgId = 500027 }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _factory.FakeCounters.GetCurrentMaxAsync("R-ER")).Should().BeNull();
    }

    [Fact]
    public async Task GenerateOrUpdateRegistrationNumber_missing_nation_and_orgid_returns_400_without_touching_the_counter()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(RegistrationNumberUrl(app), new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _factory.FakeCounters.GetCurrentMaxAsync("R-ER")).Should().BeNull();
    }

    [Fact]
    public async Task GenerateOrUpdateRegistrationNumber_invalid_nation_returns_400()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            RegistrationNumberUrl(app),
            new { nation = "Atlantis", orgId = 1 }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GenerateOrUpdateRegistrationNumber_isExporter_selects_the_X_agency_letter()
    {
        Reset();
        var app = SeedApplication(configure: a => a.IsExporter = true);

        var response = await _client.PostAsJsonAsync(
            RegistrationNumberUrl(app),
            new
            {
                nation = "Scotland",
                orgId = 7,
                year = 2026,
            }
        );

        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions
        );
        body!.RegistrationReference.Should().StartWith("R26SX");
    }

    [Fact]
    public async Task GenerateOrUpdateRegistrationNumber_glass_material_uses_glassRecyclingProcess_to_pick_the_code()
    {
        Reset();
        var app = SeedApplication(configure: a =>
        {
            a.MaterialType = MaterialType.Glass;
            a.GlassRecyclingProcess = GlassRecyclingProcess.Remelt;
        });

        var response = await _client.PostAsJsonAsync(
            RegistrationNumberUrl(app),
            new
            {
                nation = "England",
                orgId = 1,
                year = 2026,
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions
        );
        body!.RegistrationReference.Should().EndWith("GR");
    }

    [Fact]
    public async Task GenerateOrUpdateRegistrationNumber_missing_year_returns_400_without_touching_the_counter()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            RegistrationNumberUrl(app),
            new { nation = "England", orgId = 500027 }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _factory.FakeCounters.GetCurrentMaxAsync("R-ER")).Should().BeNull();
    }

    [Fact]
    public async Task GenerateOrUpdateRegistrationNumber_implausible_year_returns_400()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            RegistrationNumberUrl(app),
            new
            {
                nation = "England",
                orgId = 500027,
                year = 2126,
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
