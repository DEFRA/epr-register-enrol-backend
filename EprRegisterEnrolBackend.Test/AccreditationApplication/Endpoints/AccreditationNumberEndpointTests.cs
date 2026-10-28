using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-690: the accreditation reference is derived from the application's registration reference
// (R -> A, YY = the application's own year), never drawn from a sequence counter.
public class AccreditationNumberEndpointTests : IClassFixture<AccreditationApplicationTestFactory>
{
    private const string RegistrationReference = "R25ER0001230042PL";

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
        ApplicationStatus status = ApplicationStatus.Submitted,
        Action<AccreditationApplicationModel>? configure = null
    )
    {
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-123",
            Year = 2026,
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = status,
            RegistrationReference = RegistrationReference,
        };
        configure?.Invoke(app);
        _factory.FakePersistence.Seed(app);
        return app;
    }

    private static string AccreditationNumberUrl(AccreditationApplicationModel app) =>
        $"/api/v1/accreditation-applications/{app.OrganisationId}/{app.ApplicationId}/accreditation-number";

    private Task<HttpResponseMessage> PostWithoutBody(AccreditationApplicationModel app) =>
        _client.PostAsync(
            AccreditationNumberUrl(app),
            content: null,
            TestContext.Current.CancellationToken
        );

    private static async Task<AccreditationApplicationModel> ReadApplication(
        HttpResponseMessage response
    ) =>
        (
            await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
                JsonOptions,
                TestContext.Current.CancellationToken
            )
        )!;

    [Fact]
    public async Task Derives_the_accreditation_reference_from_the_registration_reference_and_the_application_year()
    {
        Reset();
        var app = SeedApplication();

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230042PL");
    }

    [Fact]
    public async Task Persists_the_derived_reference_on_the_application()
    {
        Reset();
        var app = SeedApplication();

        await PostWithoutBody(app);

        (await StoredApplication(app)).AccreditationReference.Should().Be("A26ER0001230042PL");
    }

    [Fact]
    public async Task Takes_YY_from_the_application_year_and_ignores_an_old_shaped_body()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            AccreditationNumberUrl(app),
            new
            {
                nation = "Scotland",
                orgId = 999999,
                year = 2030,
                regenerate = true,
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230042PL");
        _factory.FakeCounters.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Accepts_an_empty_json_body()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            AccreditationNumberUrl(app),
            new { },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230042PL");
    }

    [Theory]
    [InlineData(2027, "A27ER0001230042PL")]
    [InlineData(2100, "A00ER0001230042PL")]
    public async Task Uses_the_application_year_whatever_the_registration_reference_year(
        int applicationYear,
        string expected
    )
    {
        Reset();
        var app = SeedApplication(configure: a => a.Year = applicationYear);

        var response = await PostWithoutBody(app);

        (await ReadApplication(response)).AccreditationReference.Should().Be(expected);
    }

    [Fact]
    public async Task A_retry_returns_the_existing_reference_unchanged_persists_nothing_and_ignores_regenerate()
    {
        Reset();
        var app = SeedApplication(configure: a => a.AccreditationReference = "A26ER0001230099PL");
        var versionBefore = (await StoredApplication(app)).Version;

        var response = await _client.PostAsJsonAsync(
            AccreditationNumberUrl(app),
            new { regenerate = true, year = 2027 },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230099PL");
        var stored = await StoredApplication(app);
        stored.AccreditationReference.Should().Be("A26ER0001230099PL");
        stored.Version.Should().Be(versionBefore);
        stored.PreviousAccreditationNumbers.Should().BeEmpty();
        _factory.FakeCounters.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task A_second_call_returns_the_reference_issued_by_the_first()
    {
        Reset();
        var app = SeedApplication();

        var first = await ReadApplication(await PostWithoutBody(app));
        var second = await ReadApplication(await PostWithoutBody(app));

        second.AccreditationReference.Should().Be(first.AccreditationReference);
        second.Version.Should().Be(first.Version);
        second.PreviousAccreditationNumbers.Should().BeEmpty();
    }

    [Theory]
    [InlineData("R25ER000123004PL")] // wrong length
    [InlineData("R25QR0001230042PL")] // bad agency letter
    [InlineData("R25ER0001230042GL")] // unknown material code
    [InlineData("A25ER0001230042PL")] // a registration reference must start R
    [InlineData("STUB-REG-001")]
    public async Task A_malformed_registration_reference_returns_422_naming_it_and_persists_nothing(
        string malformed
    )
    {
        Reset();
        var app = SeedApplication(configure: a => a.RegistrationReference = malformed);

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var message = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken
        );
        message.Should().Contain(malformed).And.Contain("registration reference");
        (await StoredApplication(app)).AccreditationReference.Should().BeNull();
        _factory.FakeCounters.IsEmpty.Should().BeTrue();
    }

    // RA-690: a duplicate live application for the same registration, material and year (Seed is
    // best-effort, with no unique index) derives the same reference. It must not be issued twice.
    [Fact]
    public async Task A_second_application_for_the_same_registration_and_year_returns_409_naming_the_reference_and_persists_nothing()
    {
        Reset();
        var first = SeedApplication();
        var duplicate = SeedApplication();

        var firstResponse = await PostWithoutBody(first);
        var duplicateResponse = await PostWithoutBody(duplicate);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(firstResponse))
            .AccreditationReference.Should()
            .Be("A26ER0001230042PL");
        duplicateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var message = await duplicateResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken
        );
        message.Should().Contain("A26ER0001230042PL");
        (await StoredApplication(duplicate)).AccreditationReference.Should().BeNull();

        // The holder retrying doesn't clash with itself.
        var retry = await PostWithoutBody(first);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(retry)).AccreditationReference.Should().Be("A26ER0001230042PL");
    }

    [Theory]
    [InlineData(ApplicationStatus.Saved)]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.AwaitingDecision)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task The_derived_reference_held_by_another_application_in_any_status_but_withdrawn_returns_409(
        ApplicationStatus holderStatus
    )
    {
        Reset();
        SeedApplication(
            status: holderStatus,
            configure: a => a.AccreditationReference = "A26ER0001230042PL"
        );
        var app = SeedApplication();

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StoredApplication(app)).AccreditationReference.Should().BeNull();
    }

    // RA-690: an operator who withdrew and started again derives the same reference. The withdrawn
    // record may already hold it (a withdraw racing step 1, a failed step 1 write, or the stub
    // adapter), and must not block the restart. The withdrawn record keeps its value for audit.
    [Fact]
    public async Task The_derived_reference_held_only_by_a_withdrawn_application_is_issued_and_the_withdrawn_record_keeps_it()
    {
        Reset();
        var withdrawn = SeedApplication(
            status: ApplicationStatus.Withdrawn,
            configure: a => a.AccreditationReference = "A26ER0001230042PL"
        );
        var restart = SeedApplication();

        var response = await PostWithoutBody(restart);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230042PL");
        (await StoredApplication(restart)).AccreditationReference.Should().Be("A26ER0001230042PL");
        var withdrawnRecord = await StoredApplication(withdrawn);
        withdrawnRecord.AccreditationReference.Should().Be("A26ER0001230042PL");
        withdrawnRecord.ApplicationStatus.Should().Be(ApplicationStatus.Withdrawn);
    }

    [Theory]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.Approved)]
    public async Task A_live_or_approved_holder_alongside_a_withdrawn_one_still_returns_409(
        ApplicationStatus liveStatus
    )
    {
        Reset();
        SeedApplication(
            status: ApplicationStatus.Withdrawn,
            configure: a => a.AccreditationReference = "A26ER0001230042PL"
        );
        SeedApplication(
            status: liveStatus,
            configure: a => a.AccreditationReference = "A26ER0001230042PL"
        );
        var app = SeedApplication();

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StoredApplication(app)).AccreditationReference.Should().BeNull();
    }

    [Fact]
    public async Task The_derived_reference_held_in_another_organisation_returns_409()
    {
        Reset();
        SeedApplication(configure: a =>
        {
            a.OrganisationId = "org-other";
            a.AccreditationReference = "A26ER0001230042PL";
        });
        var app = SeedApplication();

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StoredApplication(app)).AccreditationReference.Should().BeNull();
    }

    [Fact]
    public async Task A_different_reference_held_by_another_application_is_not_a_clash()
    {
        Reset();
        SeedApplication(configure: a => a.AccreditationReference = "A26ER0001230043PL");
        var app = SeedApplication();

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230042PL");
    }

    [Fact]
    public async Task No_registration_reference_returns_409_and_persists_nothing()
    {
        Reset();
        var app = SeedApplication(configure: a => a.RegistrationReference = null);

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StoredApplication(app)).AccreditationReference.Should().BeNull();
        _factory.FakeCounters.IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task A_terminal_application_returns_409_and_persists_nothing(
        ApplicationStatus status
    )
    {
        Reset();
        var app = SeedApplication(status: status);

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StoredApplication(app)).AccreditationReference.Should().BeNull();
        _factory.FakeCounters.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_application_returns_404()
    {
        Reset();

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-1/{ObjectId.GenerateNewId()}/accreditation-number",
            content: null,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.FakeCounters.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Issuing_a_reference_never_draws_from_a_sequence_counter()
    {
        Reset();
        var app = SeedApplication();

        await PostWithoutBody(app);

        _factory.FakeCounters.IsEmpty.Should().BeTrue();
    }

    private async Task<AccreditationApplicationModel> StoredApplication(
        AccreditationApplicationModel app
    ) => (await _factory.FakePersistence.GetByIdAsync(app.OrganisationId, app.ApplicationId!))!;
}

// RA-690: the source of each issued reference is logged at Information. The default test host's
// logger has Information disabled, so this uses the capturing logger factory.
public class AccreditationNumberEndpointLoggingTests
    : IClassFixture<AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory>
{
    private readonly AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationNumberEndpointLoggingTests(
        AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory factory
    )
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Logs_the_source_kind_and_source_reference_of_an_issued_reference()
    {
        _factory.FakePersistence.Clear();
        _factory.LogCapture.Clear();
        var app = BuildApplication(ApplicationStatus.Submitted);
        _factory.FakePersistence.Seed(app);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/{app.OrganisationId}/{app.ApplicationId}/accreditation-number",
            content: null,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = _factory
            .LogCapture.Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Information
                && e.Message.StartsWith(
                    "GenerateOrUpdateAccreditationNumber: issued",
                    StringComparison.Ordinal
                )
            )
            .Subject;
        entry.Message.Should().Contain("accreditationReference=A26ER0001230042PL");
        entry.Message.Should().Contain($"applicationId={app.Id}");
        entry.Message.Should().Contain("sourceKind=Registration");
        entry.Message.Should().Contain("sourceReference=R25ER0001230042PL");
        entry.Message.Should().NotContain("withdrawnHolderIds");
    }

    // RA-690: support can see why two records share a reference from the issue log line.
    [Fact]
    public async Task Names_the_withdrawn_applications_that_already_hold_the_issued_reference()
    {
        _factory.FakePersistence.Clear();
        _factory.LogCapture.Clear();
        var withdrawn = BuildApplication(ApplicationStatus.Withdrawn);
        withdrawn.AccreditationReference = "A26ER0001230042PL";
        var app = BuildApplication(ApplicationStatus.Submitted);
        _factory.FakePersistence.Seed(withdrawn);
        _factory.FakePersistence.Seed(app);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/{app.OrganisationId}/{app.ApplicationId}/accreditation-number",
            content: null,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = _factory
            .LogCapture.Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Information
                && e.Message.StartsWith(
                    "GenerateOrUpdateAccreditationNumber: issued",
                    StringComparison.Ordinal
                )
            )
            .Subject;
        entry.Message.Should().Contain("accreditationReference=A26ER0001230042PL");
        entry.Message.Should().Contain($"applicationId={app.Id}");
        entry.Message.Should().Contain("sourceKind=Registration");
        entry.Message.Should().Contain($"withdrawnHolderIds={withdrawn.Id}");
    }

    private static AccreditationApplicationModel BuildApplication(ApplicationStatus status) =>
        new()
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-123",
            Year = 2026,
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = status,
            RegistrationReference = "R25ER0001230042PL",
        };
}
