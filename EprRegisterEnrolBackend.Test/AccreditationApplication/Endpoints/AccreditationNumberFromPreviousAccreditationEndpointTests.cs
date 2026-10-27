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

// RA-690: when this backend holds an approved accreditation for the same registration, material
// and glass process in an earlier year, the new reference is the most recent one with YY swapped.
// Otherwise it falls back to the registration reference (see AccreditationNumberEndpointTests).
public class AccreditationNumberFromPreviousAccreditationEndpointTests
    : IClassFixture<AccreditationApplicationTestFactory>
{
    private const string OrganisationId = "org-123";
    private const string RegistrationId = "reg-1";

    // The application's own registration reference carries a different sequence (9999) from the
    // previous accreditation references below, so each test shows which source was used.
    private const string RegistrationReference = "R26ER0001239999PL";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationNumberFromPreviousAccreditationEndpointTests(
        AccreditationApplicationTestFactory factory
    )
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.FakePersistence.Clear();
        _factory.FakeCounters.Clear();
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCaseWorkingAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCdpUploaderService.ClearSubstitute(ClearOptions.All);
    }

    private AccreditationApplicationModel SeedApplication(
        int year,
        Action<AccreditationApplicationModel>? configure = null
    )
    {
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = OrganisationId,
            RegistrationId = RegistrationId,
            Year = year,
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = ApplicationStatus.Submitted,
            RegistrationReference = RegistrationReference,
        };
        configure?.Invoke(app);
        _factory.FakePersistence.Seed(app);
        return app;
    }

    private AccreditationApplicationModel SeedPrevious(
        int year,
        string? accreditationReference,
        Action<AccreditationApplicationModel>? configure = null
    ) =>
        SeedApplication(
            year,
            a =>
            {
                a.ApplicationStatus = ApplicationStatus.Approved;
                a.AccreditationReference = accreditationReference;
                configure?.Invoke(a);
            }
        );

    private async Task<HttpResponseMessage> Post(AccreditationApplicationModel app) =>
        await _client.PostAsync(
            $"/api/v1/accreditation-applications/{app.OrganisationId}/{app.ApplicationId}/accreditation-number",
            content: null,
            TestContext.Current.CancellationToken
        );

    private async Task<string?> IssuedReference(AccreditationApplicationModel app)
    {
        var response = await Post(app);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        return body!.AccreditationReference;
    }

    private async Task<AccreditationApplicationModel> Stored(AccreditationApplicationModel app) =>
        (await _factory.FakePersistence.GetByIdAsync(app.OrganisationId, app.ApplicationId!))!;

    [Fact]
    public async Task A_consecutive_year_renewal_keeps_the_previous_reference_with_the_new_year()
    {
        SeedPrevious(2026, "A26ER0001230042PL");
        var app = SeedApplication(2027);

        (await IssuedReference(app)).Should().Be("A27ER0001230042PL");
        (await Stored(app)).AccreditationReference.Should().Be("A27ER0001230042PL");
    }

    [Fact]
    public async Task A_lapsed_renewal_derives_from_the_previous_reference_across_the_gap()
    {
        SeedPrevious(2026, "A26ER0001230042PL");
        var app = SeedApplication(2028);

        (await IssuedReference(app)).Should().Be("A28ER0001230042PL");
    }

    [Fact]
    public async Task The_most_recent_earlier_year_wins_over_an_older_one()
    {
        SeedPrevious(2026, "A26ER0001230042PL");
        SeedPrevious(2027, "A27ER0001230055PL");
        var app = SeedApplication(2028);

        (await IssuedReference(app)).Should().Be("A28ER0001230055PL");
    }

    [Fact]
    public async Task Within_the_same_earlier_year_the_most_recently_edited_wins()
    {
        // The older one is seeded first, so a missing tie-break can't pass by returning the
        // first-seeded application.
        SeedPrevious(
            2026,
            "A26ER0001230077PL",
            a => a.DateLastEdited = DateTime.UtcNow.AddDays(-2)
        );
        SeedPrevious(
            2026,
            "A26ER0001230042PL",
            a => a.DateLastEdited = DateTime.UtcNow.AddDays(-1)
        );
        var app = SeedApplication(2027);

        (await IssuedReference(app)).Should().Be("A27ER0001230042PL");
    }

    [Theory]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.AwaitingDecision)]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task An_earlier_application_that_is_not_approved_is_ignored(
        ApplicationStatus status
    )
    {
        SeedPrevious(2026, "A26ER0001230042PL", a => a.ApplicationStatus = status);
        var app = SeedApplication(2027);

        (await IssuedReference(app)).Should().Be("A27ER0001239999PL");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task An_earlier_approved_application_without_an_accreditation_reference_is_ignored(
        string? accreditationReference
    )
    {
        SeedPrevious(2026, accreditationReference);
        var app = SeedApplication(2027);

        (await IssuedReference(app)).Should().Be("A27ER0001239999PL");
    }

    public static TheoryData<string, Action<AccreditationApplicationModel>> UnrelatedPrevious =>
        new()
        {
            { "another organisation", a => a.OrganisationId = "org-other" },
            { "another registration", a => a.RegistrationId = "reg-other" },
            { "another material", a => a.MaterialType = MaterialType.Steel },
            { "the same year", a => a.Year = 2027 },
            { "a later year", a => a.Year = 2028 },
        };

    [Theory]
    [MemberData(nameof(UnrelatedPrevious))]
    public async Task An_approved_application_that_does_not_match_is_ignored(
        string because,
        Action<AccreditationApplicationModel> unrelate
    )
    {
        SeedPrevious(2026, "A26ER0001230042PL", unrelate);
        var app = SeedApplication(2027);

        (await IssuedReference(app)).Should().Be("A27ER0001239999PL", because);
    }

    [Fact]
    public async Task A_glass_accreditation_never_inherits_the_other_glass_process_reference()
    {
        SeedPrevious(
            2026,
            "A26ER0001230042GR",
            a =>
            {
                a.MaterialType = MaterialType.Glass;
                a.GlassRecyclingProcess = GlassRecyclingProcess.Remelt;
            }
        );
        var app = SeedApplication(
            2027,
            a =>
            {
                a.MaterialType = MaterialType.Glass;
                a.GlassRecyclingProcess = GlassRecyclingProcess.Other;
                a.RegistrationReference = "R26ER0001239999GO";
            }
        );

        (await IssuedReference(app)).Should().Be("A27ER0001239999GO");
    }

    [Fact]
    public async Task A_glass_accreditation_inherits_the_same_glass_process_reference()
    {
        SeedPrevious(
            2026,
            "A26ER0001230042GR",
            a =>
            {
                a.MaterialType = MaterialType.Glass;
                a.GlassRecyclingProcess = GlassRecyclingProcess.Remelt;
            }
        );
        var app = SeedApplication(
            2027,
            a =>
            {
                a.MaterialType = MaterialType.Glass;
                a.GlassRecyclingProcess = GlassRecyclingProcess.Remelt;
                a.RegistrationReference = "R26ER0001239999GR";
            }
        );

        (await IssuedReference(app)).Should().Be("A27ER0001230042GR");
    }

    [Fact]
    public async Task A_2026_application_with_no_earlier_history_derives_from_the_registration_reference()
    {
        var app = SeedApplication(2026);

        (await IssuedReference(app)).Should().Be("A26ER0001239999PL");
    }

    [Fact]
    public async Task An_application_without_a_registration_id_derives_from_the_registration_reference()
    {
        SeedPrevious(2026, "A26ER0001230042PL", a => a.RegistrationId = null);
        var app = SeedApplication(2027, a => a.RegistrationId = null);

        (await IssuedReference(app)).Should().Be("A27ER0001239999PL");
    }

    [Fact]
    public async Task A_malformed_previous_reference_returns_422_naming_it_without_falling_back()
    {
        SeedPrevious(2026, "A26ER000123004PL");
        var app = SeedApplication(2027);

        var response = await Post(app);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var message = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken
        );
        message.Should().Contain("A26ER000123004PL").And.Contain("previous accreditation");
        (await Stored(app)).AccreditationReference.Should().BeNull();
    }

    [Fact]
    public async Task Deriving_from_a_previous_reference_never_draws_from_a_sequence_counter()
    {
        SeedPrevious(2026, "A26ER0001230042PL");
        var app = SeedApplication(2027);

        await IssuedReference(app);

        _factory.FakeCounters.IsEmpty.Should().BeTrue();
    }
}

// RA-690: for a previous-accreditation source the Information log also names the source
// application, so support can trace the reference back.
public class AccreditationNumberFromPreviousAccreditationLoggingTests
    : IClassFixture<AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory>
{
    private readonly AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationNumberFromPreviousAccreditationLoggingTests(
        AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory factory
    )
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Logs_the_previous_accreditation_source_and_its_application_id()
    {
        _factory.FakePersistence.Clear();
        _factory.LogCapture.Clear();
        var previous = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-123",
            RegistrationId = "reg-1",
            Year = 2026,
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = ApplicationStatus.Approved,
            RegistrationReference = "R25ER0001230042PL",
            AccreditationReference = "A26ER0001230042PL",
        };
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-123",
            RegistrationId = "reg-1",
            Year = 2027,
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = ApplicationStatus.Submitted,
            RegistrationReference = "R26ER0001239999PL",
        };
        _factory.FakePersistence.Seed(previous);
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
        entry.Message.Should().Contain("accreditationReference=A27ER0001230042PL");
        entry.Message.Should().Contain("sourceKind=PreviousAccreditation");
        entry.Message.Should().Contain("sourceReference=A26ER0001230042PL");
        entry.Message.Should().Contain($"sourceApplicationId={previous.Id}");
    }
}
