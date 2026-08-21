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

public class AccreditationNumberEndpointTests : IClassFixture<AccreditationApplicationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationNumberEndpointTests(AccreditationApplicationTestFactory factory)
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
            // AC9: an accreditation number always needs an existing registration
            // number, so every test seeds one unless it's specifically testing AC9.
            RegistrationReference = "R26ER5000270001WO",
        };
        configure?.Invoke(app);
        _factory.FakePersistence.Seed(app);
        return app;
    }

    private string AccreditationNumberUrl(AccreditationApplicationModel app) =>
        $"/api/v1/accreditation-applications/{app.OrganisationId}/{app.ApplicationId}/accreditation-number";

    [Fact]
    public async Task GenerateOrUpdateAccreditationNumber_no_existing_number_generates_and_persists_one()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            AccreditationNumberUrl(app),
            new
            {
                nation = "England",
                orgId = 500027,
                year = 2026,
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        body!.AccreditationReference.Should().Be("A26ER5000270001WO");
    }

    [Fact]
    public async Task GenerateOrUpdateAccreditationNumber_no_registration_number_yet_returns_409_without_touching_the_counter()
    {
        Reset();
        var app = SeedApplication(configure: a => a.RegistrationReference = null);

        var response = await _client.PostAsJsonAsync(
            AccreditationNumberUrl(app),
            new { nation = "England", orgId = 500027 },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (
            await _factory.FakeCounters.GetCurrentMaxAsync(
                "A-ER",
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task GenerateOrUpdateAccreditationNumber_idempotent_recall_returns_existing_number_unchanged()
    {
        Reset();
        var app = SeedApplication(configure: a => a.AccreditationReference = "A26ER5000270063WO");

        var response = await _client.PostAsJsonAsync(
            AccreditationNumberUrl(app),
            new { nation = "England", orgId = 999999 },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        body!.AccreditationReference.Should().Be("A26ER5000270063WO");
        (
            await _factory.FakeCounters.GetCurrentMaxAsync(
                "A-ER",
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task GenerateOrUpdateAccreditationNumber_regenerate_increments_only_the_year_and_does_not_touch_the_counter()
    {
        Reset();
        var app = SeedApplication(configure: a => a.AccreditationReference = "A26ER5000270063WO");

        var response = await _client.PostAsJsonAsync(
            AccreditationNumberUrl(app),
            new { regenerate = true },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        body!.AccreditationReference.Should().Be("A27ER5000270063WO");
        body.PreviousAccreditationNumbers.Should()
            .ContainSingle()
            .Which.Should()
            .Be("A26ER5000270063WO");
        (
            await _factory.FakeCounters.GetCurrentMaxAsync(
                "A-ER",
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task GenerateOrUpdateAccreditationNumber_regenerate_does_not_require_nation_or_orgid()
    {
        // Reapply is a pure string transform - it must not demand caller data it
        // doesn't need, unlike a first-ever generate.
        Reset();
        var app = SeedApplication(configure: a => a.AccreditationReference = "A26ER5000270063WO");

        var response = await _client.PostAsJsonAsync(
            AccreditationNumberUrl(app),
            new { regenerate = true },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GenerateOrUpdateAccreditationNumber_first_generate_with_regenerate_flag_set_still_draws_from_the_counter()
    {
        // Regenerate is meaningless (and ignored) when there's no existing number -
        // this is just a first-ever generate, so it must go through the counter,
        // not the year-increment path (which has nothing to increment).
        Reset();
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            AccreditationNumberUrl(app),
            new
            {
                nation = "England",
                orgId = 500027,
                year = 2026,
                regenerate = true,
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        body!.AccreditationReference.Should().Be("A26ER5000270001WO");
        (
            await _factory.FakeCounters.GetCurrentMaxAsync(
                "A-ER",
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task GenerateOrUpdateAccreditationNumber_and_registration_pools_are_independent()
    {
        Reset();
        var regApp = SeedApplication(
            orgId: "org-reg",
            configure: a => a.RegistrationReference = null
        );
        var accApp = SeedApplication(orgId: "org-acc");

        // Regenerating the registration number draws from R-ER - this must have no
        // effect whatsoever on A-ER's own counter (AC2: separate counters).
        await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/{regApp.OrganisationId}/{regApp.ApplicationId}/registration-number",
            new
            {
                nation = "England",
                orgId = 1,
                year = 2026,
            },
            TestContext.Current.CancellationToken
        );

        var response = await _client.PostAsJsonAsync(
            AccreditationNumberUrl(accApp),
            new
            {
                nation = "England",
                orgId = 3,
                year = 2026,
            },
            TestContext.Current.CancellationToken
        );

        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        (
            await _factory.FakeCounters.GetCurrentMaxAsync(
                "R-ER",
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(1);
        body!.AccreditationReference.Should().Be("A26ER0000030001WO");
    }

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task GenerateOrUpdateAccreditationNumber_terminal_status_returns_409_without_touching_the_counter(
        ApplicationStatus status
    )
    {
        Reset();
        var app = SeedApplication(status: status);

        var response = await _client.PostAsJsonAsync(
            AccreditationNumberUrl(app),
            new { nation = "England", orgId = 500027 },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (
            await _factory.FakeCounters.GetCurrentMaxAsync(
                "A-ER",
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task GenerateOrUpdateAccreditationNumber_missing_application_returns_404()
    {
        Reset();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-1/{ObjectId.GenerateNewId()}/accreditation-number",
            new { nation = "England", orgId = 1 },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GenerateOrUpdateAccreditationNumber_missing_nation_and_orgid_on_first_generate_returns_400_without_touching_the_counter()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            AccreditationNumberUrl(app),
            new { },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (
            await _factory.FakeCounters.GetCurrentMaxAsync(
                "A-ER",
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task GenerateOrUpdateAccreditationNumber_missing_year_on_first_generate_returns_400_without_touching_the_counter()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            AccreditationNumberUrl(app),
            new { nation = "England", orgId = 500027 },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (
            await _factory.FakeCounters.GetCurrentMaxAsync(
                "A-ER",
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .BeNull();
    }
}
