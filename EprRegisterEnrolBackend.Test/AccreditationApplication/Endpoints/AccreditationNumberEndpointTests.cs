using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-690/RA-783: a first-time accreditation number takes the registration reference's agency,
// process type and OrgId, the application's year and material, and a new sequence of 0 + 3
// random digits (the fake random's default draw gives 0583). Never drawn from a sequence counter.
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
        _factory.FakeSequenceRandom.Clear();
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
    public async Task Builds_a_first_time_reference_from_the_registration_reference_the_application_year_and_a_random_sequence()
    {
        Reset();
        var app = SeedApplication();

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230583PL");
    }

    [Fact]
    public async Task Persists_the_issued_reference_on_the_application()
    {
        Reset();
        var app = SeedApplication();

        await PostWithoutBody(app);

        (await StoredApplication(app)).AccreditationReference.Should().Be("A26ER0001230583PL");
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
        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230583PL");
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
        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230583PL");
    }

    [Theory]
    [InlineData(2027, "A27ER0001230583PL")]
    [InlineData(2100, "A00ER0001230583PL")]
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
    [InlineData("R25ER0001230042XX")] // unknown material code
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
        message.Should().Contain(malformed).And.Contain("registration number");
        (await StoredApplication(app)).AccreditationReference.Should().BeNull();
        _factory.FakeCounters.IsEmpty.Should().BeTrue();
    }

    // RA-783 AC04: a duplicate live application for the same registration and year gets a
    // different number. The first's number is one of the organisation's, so the draw avoids it.
    [Fact]
    public async Task A_duplicate_application_for_the_same_registration_gets_a_different_number()
    {
        Reset();
        var first = SeedApplication();
        var duplicate = SeedApplication();
        _factory.FakeSequenceRandom.Enqueue(583, 583, 584);

        var firstResponse = await PostWithoutBody(first);
        var duplicateResponse = await PostWithoutBody(duplicate);

        (await ReadApplication(firstResponse))
            .AccreditationReference.Should()
            .Be("A26ER0001230583PL");
        duplicateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(duplicateResponse))
            .AccreditationReference.Should()
            .Be("A26ER0001230584PL");

        // The holder retrying doesn't clash with itself.
        var retry = await PostWithoutBody(first);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(retry)).AccreditationReference.Should().Be("A26ER0001230583PL");
    }

    [Fact]
    public async Task Gives_up_with_409_when_every_random_number_drawn_is_already_held()
    {
        Reset();
        // In another organisation, so the draw doesn't already avoid it as one of this org's.
        SeedApplication(configure: a =>
        {
            a.OrganisationId = "org-other";
            a.AccreditationReference = "A26ER0001230583PL";
        });
        var app = SeedApplication();

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var message = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken
        );
        message.Should()
            .Contain($"{AccreditationApplicationEndpoints.MaxAccreditationNumberAttempts} attempts");
        (await StoredApplication(app)).AccreditationReference.Should().BeNull();
    }

    // RA-783 AC04: another site of the same organisation holds last year's number 0164 for its
    // renewal, not yet issued. A first-time number here must not take 0164, or that renewal could
    // never be issued.
    [Fact]
    public async Task A_new_number_never_takes_another_sites_renewal_sequence()
    {
        Reset();
        SeedApplication(configure: a =>
        {
            a.RegistrationId = "reg-other-site";
            a.RegistrationReference = "R25ER0001230077PL";
            a.LastYearsAccreditationNumber = new AccreditationNumberForYearModel
            {
                Year = 2025,
                AccreditationNumber = "A25ER0001230164PL",
            };
        });
        var app = SeedApplication(configure: a => a.RegistrationId = "reg-this-site");
        _factory.FakeSequenceRandom.Enqueue(164, 583);

        var response = await PostWithoutBody(app);

        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230583PL");
    }

    // RA-783 AC04: a site accredited in 2026 that hasn't started an application here is only on the
    // 2026 public register. J. and A. Young (500095) has four plastic sites there, one A26ER5000950064PL;
    // a new number for another of its sites must not take 0064, or that renewal could never be issued.
    [Fact]
    public async Task A_new_number_never_takes_a_sequence_on_the_2026_public_register()
    {
        Reset();
        var app = SeedApplication(configure: a =>
        {
            a.Year = 2027;
            a.RegistrationReference = "R26ER5000950099PL";
        });
        _factory.FakeSequenceRandom.Enqueue(64, 583);

        var response = await PostWithoutBody(app);

        (await ReadApplication(response)).AccreditationReference.Should().Be("A27ER5000950583PL");
    }

    [Fact]
    public async Task A_new_number_never_takes_a_sequence_another_site_was_issued_in_an_earlier_year()
    {
        Reset();
        SeedApplication(
            status: ApplicationStatus.Approved,
            configure: a =>
            {
                a.Year = 2025;
                a.RegistrationId = "reg-other-site";
                a.AccreditationReference = "A25ER0001230583PL";
            }
        );
        var app = SeedApplication(configure: a => a.RegistrationId = "reg-this-site");
        _factory.FakeSequenceRandom.Enqueue(583, 584);

        var response = await PostWithoutBody(app);

        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230584PL");
    }

    [Fact]
    public async Task Never_reuses_the_registration_references_sequence()
    {
        Reset();
        var app = SeedApplication();
        _factory.FakeSequenceRandom.Enqueue(42, 100);

        var response = await PostWithoutBody(app);

        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230100PL");
    }

    [Theory]
    [InlineData("R25ER0001230042GL", GlassRecyclingProcess.Remelt, "A26ER0001230583GR")]
    [InlineData("R25ER0001230042GL", GlassRecyclingProcess.Other, "A26ER0001230583GO")]
    [InlineData("R25ER0001230042GR", GlassRecyclingProcess.Other, "A26ER0001230583GO")]
    public async Task Glass_takes_GR_or_GO_from_the_applications_recycling_process(
        string registrationReference,
        GlassRecyclingProcess process,
        string expected
    )
    {
        Reset();
        var app = SeedApplication(configure: a =>
        {
            a.MaterialType = MaterialType.Glass;
            a.GlassRecyclingProcess = process;
            a.RegistrationReference = registrationReference;
        });

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(response)).AccreditationReference.Should().Be(expected);
    }

    // RA-783: ReEx gave no glass process, so the number is issued as other (GO), which case
    // management accepts, rather than refusing approval. The error log is in the logging tests.
    [Fact]
    public async Task Glass_without_a_recycling_process_is_issued_as_other()
    {
        Reset();
        var app = SeedApplication(configure: a =>
        {
            a.MaterialType = MaterialType.Glass;
            a.GlassRecyclingProcess = null;
            a.RegistrationReference = "R25ER0001230042GL";
        });

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230583GO");
    }

    // RA-783 AC04: a new random number held by any other application, withdrawn included, is drawn
    // again, and the holder keeps its value. The holder is in another organisation, so its number
    // isn't one the draw already avoids: only the holder check can cause the redraw.
    [Theory]
    [InlineData(ApplicationStatus.Saved)]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.AwaitingDecision)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task A_new_number_held_by_another_application_in_any_status_is_drawn_again(
        ApplicationStatus holderStatus
    )
    {
        Reset();
        var holder = SeedApplication(
            status: holderStatus,
            configure: a =>
            {
                a.OrganisationId = "org-other";
                a.AccreditationReference = "A26ER0001230583PL";
            }
        );
        var app = SeedApplication();
        _factory.FakeSequenceRandom.Enqueue(583, 584);

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230584PL");
        var holderRecord = await StoredApplication(holder);
        holderRecord.AccreditationReference.Should().Be("A26ER0001230583PL");
        holderRecord.ApplicationStatus.Should().Be(holderStatus);
    }

    [Fact]
    public async Task A_different_reference_held_by_another_application_is_not_a_clash()
    {
        Reset();
        SeedApplication(configure: a => a.AccreditationReference = "A26ER0001230584PL");
        var app = SeedApplication();

        var response = await PostWithoutBody(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplication(response)).AccreditationReference.Should().Be("A26ER0001230583PL");
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
        _factory.FakeSequenceRandom.Clear();
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
        entry.Message.Should().Contain("accreditationReference=A26ER0001230583PL");
        entry.Message.Should().Contain($"applicationId={app.Id}");
        entry.Message.Should().Contain("case=FirstTime");
        entry.Message.Should().Contain("sourceKind=Registration");
        entry.Message.Should().Contain("sourceReference=R25ER0001230042PL");
        entry.Message.Should().NotContain("withdrawnHolderIds");
    }

    // RA-783: a new number held by a withdrawn application is drawn again, so the log never names
    // a withdrawn holder for one (a restarted renewal still does).
    [Fact]
    public async Task A_new_number_held_by_a_withdrawn_application_is_drawn_again_and_logs_no_withdrawn_holder()
    {
        _factory.FakePersistence.Clear();
        _factory.FakeSequenceRandom.Clear();
        _factory.LogCapture.Clear();
        // In another organisation, so only the holder check can cause the redraw.
        var withdrawn = BuildApplication(ApplicationStatus.Withdrawn);
        withdrawn.OrganisationId = "org-other";
        withdrawn.AccreditationReference = "A26ER0001230583PL";
        var app = BuildApplication(ApplicationStatus.Submitted);
        _factory.FakePersistence.Seed(withdrawn);
        _factory.FakePersistence.Seed(app);
        _factory.FakeSequenceRandom.Enqueue(583, 584);

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
        entry.Message.Should().Contain("accreditationReference=A26ER0001230584PL");
        entry.Message.Should().Contain("case=FirstTime");
        entry.Message.Should().NotContain("withdrawnHolderIds");
    }

    // RA-783: a glass number issued as other because no process was held is logged as an error
    // naming the application, organisation, registration and the number issued.
    [Fact]
    public async Task Logs_an_error_when_glass_is_issued_as_other_for_want_of_a_process()
    {
        _factory.FakePersistence.Clear();
        _factory.FakeSequenceRandom.Clear();
        _factory.LogCapture.Clear();
        var app = BuildApplication(ApplicationStatus.Submitted);
        app.MaterialType = MaterialType.Glass;
        app.GlassRecyclingProcess = null;
        app.RegistrationReference = "R25ER0001230042GL";
        app.ApplicationReference = "AP26EA000123GL";
        app.OrgId = 123;
        _factory.FakePersistence.Seed(app);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/{app.OrganisationId}/{app.ApplicationId}/accreditation-number",
            content: null,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = _factory
            .LogCapture.Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error)
            .Subject;
        entry.Message.Should().Contain("no glass recycling process");
        entry.Message.Should().Contain("applicationReference=AP26EA000123GL");
        entry.Message.Should().Contain($"applicationId={app.Id}");
        entry.Message.Should().Contain("organisationId=org-123");
        entry.Message.Should().Contain("orgId=123");
        entry.Message.Should().Contain("registrationReference=R25ER0001230042GL");
        entry.Message.Should().Contain("accreditationReference=A26ER0001230583GO");
    }

    [Fact]
    public async Task Logs_no_error_for_glass_with_a_recycling_process()
    {
        _factory.FakePersistence.Clear();
        _factory.FakeSequenceRandom.Clear();
        _factory.LogCapture.Clear();
        var app = BuildApplication(ApplicationStatus.Submitted);
        app.MaterialType = MaterialType.Glass;
        app.GlassRecyclingProcess = GlassRecyclingProcess.Remelt;
        app.RegistrationReference = "R25ER0001230042GR";
        _factory.FakePersistence.Seed(app);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/{app.OrganisationId}/{app.ApplicationId}/accreditation-number",
            content: null,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LogCapture.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
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
