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

// RA-763: a 2027 renewal's reference is last year's accreditation number, kept from ReEx at Seed,
// with YY swapped. A previous accreditation held in this backend comes first (see
// AccreditationNumberFromPreviousAccreditationEndpointTests); an application holding neither gets
// a first-time number (RA-783: the registration's agency and OrgId with a random sequence).
public class AccreditationNumberFromLastYearsAccreditationNumberEndpointTests
    : IClassFixture<AccreditationApplicationTestFactory>
{
    private const string OrganisationId = "org-123";
    private const string RegistrationId = "reg-1";

    // The registration reference carries a different sequence (9999) from last year's
    // accreditation numbers below, as most do in the public register, so each test shows which
    // source was used.
    private const string RegistrationReference = "R26ER0001239999PL";
    private const string LastYearsNumber = "A26ER0001230042PL";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationNumberFromLastYearsAccreditationNumberEndpointTests(
        AccreditationApplicationTestFactory factory
    )
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.FakePersistence.Clear();
        _factory.FakeCounters.Clear();
        _factory.FakeSequenceRandom.Clear();
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCaseWorkingAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCdpUploaderService.ClearSubstitute(ClearOptions.All);
    }

    private static AccreditationNumberForYearModel LastYears(string number, int year = 2026) =>
        new() { Year = year, AccreditationNumber = number };

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
    public async Task A_renewal_keeps_last_years_accreditation_number_with_the_new_year()
    {
        var app = SeedApplication(2027, a => a.LastYearsAccreditationNumber = LastYears(LastYearsNumber));

        (await IssuedReference(app)).Should().Be("A27ER0001230042PL");
        (await Stored(app)).AccreditationReference.Should().Be("A27ER0001230042PL");
    }

    [Fact]
    public async Task An_application_holding_no_last_years_number_gets_a_first_time_number()
    {
        var app = SeedApplication(2027);

        (await IssuedReference(app)).Should().Be("A27ER0001230583PL");
    }

    // An accreditation this service issued (2028 onwards) is held here and ReEx doesn't have it,
    // so it comes before last year's number from ReEx.
    [Fact]
    public async Task An_earlier_approved_application_held_here_comes_before_last_years_number()
    {
        SeedApplication(
            2026,
            a =>
            {
                a.ApplicationStatus = ApplicationStatus.Approved;
                a.AccreditationReference = "A26ER0001230055PL";
            }
        );
        var withLastYears = SeedApplication(
            2027,
            a => a.LastYearsAccreditationNumber = LastYears(LastYearsNumber)
        );

        (await IssuedReference(withLastYears)).Should().Be("A27ER0001230055PL");
    }

    [Fact]
    public async Task A_glass_renewal_keeps_last_years_glass_process_code()
    {
        var app = SeedApplication(
            2027,
            a =>
            {
                a.MaterialType = MaterialType.Glass;
                a.GlassRecyclingProcess = GlassRecyclingProcess.Remelt;
                a.RegistrationReference = "R26ER0001239999GR";
                a.LastYearsAccreditationNumber = LastYears("A26ER0001230042GR");
            }
        );

        (await IssuedReference(app)).Should().Be("A27ER0001230042GR");
    }

    // RA-783 AC07: 2026 glass numbers from ReEx end in GL; the renewal keeps the sequence and
    // takes GR or GO from this year's recycling process.
    [Theory]
    [InlineData(GlassRecyclingProcess.Remelt, "A27ER0001230042GR")]
    [InlineData(GlassRecyclingProcess.Other, "A27ER0001230042GO")]
    public async Task A_glass_renewal_from_a_GL_number_takes_this_years_recycling_process(
        GlassRecyclingProcess process,
        string expected
    )
    {
        var app = SeedApplication(
            2027,
            a =>
            {
                a.MaterialType = MaterialType.Glass;
                a.GlassRecyclingProcess = process;
                a.RegistrationReference = "R26ER0001239999GL";
                a.LastYearsAccreditationNumber = LastYears("A26ER0001230042GL");
            }
        );

        (await IssuedReference(app)).Should().Be(expected);
    }

    [Fact]
    public async Task A_malformed_last_years_number_returns_422_naming_it_without_falling_back()
    {
        var app = SeedApplication(
            2027,
            a => a.LastYearsAccreditationNumber = LastYears("A26ER000123004PL")
        );

        var response = await Post(app);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var message = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken
        );
        message.Should().Contain("A26ER000123004PL").And.Contain("Last year's accreditation number");
        (await Stored(app)).AccreditationReference.Should().BeNull();
    }

    [Fact]
    public async Task A_last_years_number_for_another_year_returns_422_without_falling_back()
    {
        var app = SeedApplication(
            2027,
            a => a.LastYearsAccreditationNumber = LastYears("A25ER0001230042PL", year: 2025)
        );

        var response = await Post(app);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var message = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken
        );
        message.Should().Contain("A25ER0001230042PL").And.Contain("2025, not 2026");
        (await Stored(app)).AccreditationReference.Should().BeNull();
    }

    // The year is only checked when last year's number is the source.
    [Fact]
    public async Task A_last_years_number_for_another_year_is_not_checked_when_an_earlier_approval_is_held()
    {
        SeedApplication(
            2026,
            a =>
            {
                a.ApplicationStatus = ApplicationStatus.Approved;
                a.AccreditationReference = "A26ER0001230055PL";
            }
        );
        var app = SeedApplication(
            2027,
            a => a.LastYearsAccreditationNumber = LastYears("A25ER0001230042PL", year: 2025)
        );

        (await IssuedReference(app)).Should().Be("A27ER0001230055PL");
    }

    // RA-783: a renewal's number is fixed, so another live application holding it is a conflict,
    // not a reason to draw again.
    [Fact]
    public async Task A_renewal_whose_number_is_already_held_returns_409_naming_it()
    {
        SeedApplication(
            2027,
            a =>
            {
                a.RegistrationId = "reg-other";
                a.AccreditationReference = "A27ER0001230042PL";
            }
        );
        var app = SeedApplication(2027, a => a.LastYearsAccreditationNumber = LastYears(LastYearsNumber));

        var response = await Post(app);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var message = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken
        );
        message.Should().Contain("A27ER0001230042PL").And.NotContain("attempts");
        (await Stored(app)).AccreditationReference.Should().BeNull();
    }

    // With an earlier approval held here, a last year's number for the wrong year is ignored: the
    // held approval is the history, so a 2026 approval and a 2029 application is a renewal after a
    // break, whatever the wrong-year number says.
    [Fact]
    public async Task A_wrong_year_last_years_number_is_ignored_when_an_earlier_approval_is_held()
    {
        SeedApplication(
            2026,
            a =>
            {
                a.ApplicationStatus = ApplicationStatus.Approved;
                a.AccreditationReference = "A26ER0001230055PL";
            }
        );
        var app = SeedApplication(
            2029,
            a => a.LastYearsAccreditationNumber = LastYears("A27ER0001230042PL", year: 2027)
        );

        (await IssuedReference(app)).Should().Be("A29ER0001230583PL");
    }

    [Fact]
    public async Task Deriving_from_last_years_number_never_draws_from_a_sequence_counter()
    {
        var app = SeedApplication(2027, a => a.LastYearsAccreditationNumber = LastYears(LastYearsNumber));

        await IssuedReference(app);

        _factory.FakeCounters.IsEmpty.Should().BeTrue();
    }
}

// RA-690: each issued reference's source is logged, so support can trace it back.
public class AccreditationNumberFromLastYearsAccreditationNumberLoggingTests
    : IClassFixture<AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory>
{
    private readonly AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationNumberFromLastYearsAccreditationNumberLoggingTests(
        AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory factory
    )
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Logs_last_years_accreditation_number_as_the_source()
    {
        _factory.FakePersistence.Clear();
        _factory.LogCapture.Clear();
        var app = SeedRenewal(ApplicationStatus.Submitted, accreditationReference: null);

        var response = await Post(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = IssuedLogEntry();
        entry.Should().Contain("accreditationReference=A27ER0001230042PL");
        entry.Should().Contain("case=ContinuousRenewal");
        entry.Should().Contain("sourceKind=LastYearsAccreditationNumber");
        entry.Should().Contain("sourceReference=A26ER0001230042PL");
        entry.Should().NotContain("withdrawnHolderIds");
    }

    // RA-690: a renewal that was withdrawn and restarted derives the same reference again, and
    // the log names the withdrawn holder.
    [Fact]
    public async Task A_restarted_renewal_logs_the_withdrawn_holder()
    {
        _factory.FakePersistence.Clear();
        _factory.LogCapture.Clear();
        var withdrawn = SeedRenewal(ApplicationStatus.Withdrawn, "A27ER0001230042PL");
        var app = SeedRenewal(ApplicationStatus.Submitted, accreditationReference: null);

        var response = await Post(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = IssuedLogEntry();
        entry.Should().Contain("accreditationReference=A27ER0001230042PL");
        entry.Should().Contain("sourceKind=LastYearsAccreditationNumber");
        entry.Should().Contain($"withdrawnHolderIds={withdrawn.Id}");
    }

    private Task<HttpResponseMessage> Post(AccreditationApplicationModel app) =>
        _client.PostAsync(
            $"/api/v1/accreditation-applications/{app.OrganisationId}/{app.ApplicationId}/accreditation-number",
            content: null,
            TestContext.Current.CancellationToken
        );

    private string IssuedLogEntry() =>
        _factory
            .LogCapture.Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Information
                && e.Message.StartsWith(
                    "GenerateOrUpdateAccreditationNumber: issued",
                    StringComparison.Ordinal
                )
            )
            .Subject.Message;

    private AccreditationApplicationModel SeedRenewal(
        ApplicationStatus status,
        string? accreditationReference
    )
    {
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-123",
            RegistrationId = "reg-1",
            Year = 2027,
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = status,
            RegistrationReference = "R26ER0001239999PL",
            AccreditationReference = accreditationReference,
            LastYearsAccreditationNumber = new AccreditationNumberForYearModel
            {
                Year = 2026,
                AccreditationNumber = "A26ER0001230042PL",
            },
        };
        _factory.FakePersistence.Seed(app);
        return app;
    }
}