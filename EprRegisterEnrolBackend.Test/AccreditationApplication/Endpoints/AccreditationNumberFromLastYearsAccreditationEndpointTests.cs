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
// AccreditationNumberFromPreviousAccreditationEndpointTests); an application holding neither
// derives from the registration reference.
public class AccreditationNumberFromLastYearsAccreditationEndpointTests
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

    public AccreditationNumberFromLastYearsAccreditationEndpointTests(
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
        var app = SeedApplication(2027, a => a.LastYearsAccreditation = LastYears(LastYearsNumber));

        (await IssuedReference(app)).Should().Be("A27ER0001230042PL");
        (await Stored(app)).AccreditationReference.Should().Be("A27ER0001230042PL");
    }

    [Fact]
    public async Task An_application_holding_no_last_years_number_derives_from_the_registration_reference()
    {
        var app = SeedApplication(2027);

        (await IssuedReference(app)).Should().Be("A27ER0001239999PL");
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
            a => a.LastYearsAccreditation = LastYears(LastYearsNumber)
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
                a.LastYearsAccreditation = LastYears("A26ER0001230042GR");
            }
        );

        (await IssuedReference(app)).Should().Be("A27ER0001230042GR");
    }

    [Fact]
    public async Task A_malformed_last_years_number_returns_422_naming_it_without_falling_back()
    {
        var app = SeedApplication(
            2027,
            a => a.LastYearsAccreditation = LastYears("A26ER000123004PL")
        );

        var response = await Post(app);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var message = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken
        );
        message.Should().Contain("A26ER000123004PL").And.Contain("previous accreditation");
        (await Stored(app)).AccreditationReference.Should().BeNull();
    }

    [Fact]
    public async Task Deriving_from_last_years_number_never_draws_from_a_sequence_counter()
    {
        var app = SeedApplication(2027, a => a.LastYearsAccreditation = LastYears(LastYearsNumber));

        await IssuedReference(app);

        _factory.FakeCounters.IsEmpty.Should().BeTrue();
    }
}

// RA-690: each issued reference's source is logged, so support can trace it back.
public class AccreditationNumberFromLastYearsAccreditationLoggingTests
    : IClassFixture<AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory>
{
    private readonly AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationNumberFromLastYearsAccreditationLoggingTests(
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
        entry.Should().Contain("sourceKind=PreviousAccreditation");
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
        entry.Should().Contain("sourceKind=PreviousAccreditation");
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
            LastYearsAccreditation = new AccreditationNumberForYearModel
            {
                Year = 2026,
                AccreditationNumber = "A26ER0001230042PL",
            },
        };
        _factory.FakePersistence.Seed(app);
        return app;
    }
}
