using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.ReEx;
using FluentAssertions;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-765: seeding year Y copies the operator's latest granted accreditation, not only Y-1's, so an
// operator whose accreditation lapsed (granted Y-2, nothing for Y-1) still starts from it. The source
// year is the latest Approved application held here for the same organisation, registration and
// material before Y (RA-690's lookup), or Y-1 when there is none (accreditations granted in ReEx)
// or ReEx doesn't hold that year.
public class SeedFromLatestGrantedAccreditationEndpointTests
    : IClassFixture<AccreditationApplicationTestFactory>
{
    private const string OrganisationId = "org-123";
    private const string RegistrationId = "reg-1";
    private const int Year = 2029;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public SeedFromLatestGrantedAccreditationEndpointTests(
        AccreditationApplicationTestFactory factory
    )
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.FakePersistence.Clear();
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        // ReEx holds nothing unless a test says it holds that year's accreditation.
        _factory
            .MockReExAdapter.GetAccreditationAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MaterialType>(),
                Arg.Any<int>()
            )
            .Returns(
                Task.FromResult(
                    ReExResult<ReExAccreditationDto>.Fail(
                        new ReExError(ReExErrorKind.NotFound, "No accreditation for year"),
                        404
                    )
                )
            );
    }

    private static string NumberFor(int year) => $"A{year % 100:D2}ER0001230042PL";

    private void ReExHolds(int year) =>
        _factory
            .MockReExAdapter.GetAccreditationAsync(
                OrganisationId,
                RegistrationId,
                MaterialType.Plastic,
                year
            )
            .Returns(
                Task.FromResult(
                    ReExResult<ReExAccreditationDto>.Success(
                        new ReExAccreditationDto
                        {
                            RegistrationStatus = RegistrationStatus.Approved,
                            AccreditationId = $"reex-acc-{year}",
                            AccreditationNumber = NumberFor(year),
                            OrganisationId = OrganisationId,
                            MaterialType = MaterialType.Plastic,
                            Year = year,
                            OrganisationName = "Lapsed Ltd",
                            OverseasSites = [],
                        },
                        200
                    )
                )
            );

    private void HeldHere(
        int year,
        ApplicationStatus status = ApplicationStatus.Approved,
        Action<AccreditationApplicationModel>? configure = null
    )
    {
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = OrganisationId,
            RegistrationId = RegistrationId,
            MaterialType = MaterialType.Plastic,
            Year = year,
            ApplicationStatus = status,
            AccreditationReference = NumberFor(year),
        };
        configure?.Invoke(app);
        _factory.FakePersistence.Seed(app);
    }

    private Task<HttpResponseMessage> PostSeed() =>
        _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/{OrganisationId}/{RegistrationId}/Plastic/seed",
            new SeedRequest { Year = Year },
            cancellationToken: TestContext.Current.CancellationToken
        );

    private static async Task<AccreditationApplicationModel> Created(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (
            await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
                JsonOptions,
                TestContext.Current.CancellationToken
            )
        )!;
    }

    private static void ShouldBeSeededFrom(AccreditationApplicationModel created, int sourceYear)
    {
        created.Year.Should().Be(Year);
        created.SourceYear.Should().Be(sourceYear);
        created.SourceReExAccreditationId.Should().Be($"reex-acc-{sourceYear}");
        created
            .LastYearsAccreditationNumber.Should()
            .BeEquivalentTo(
                new AccreditationNumberForYearModel
                {
                    Year = sourceYear,
                    AccreditationNumber = NumberFor(sourceYear),
                }
            );
    }

    [Fact]
    public async Task With_nothing_held_here_seeds_from_last_years_ReEx_accreditation_as_before()
    {
        ReExHolds(Year - 1);

        ShouldBeSeededFrom(await Created(await PostSeed()), Year - 1);
    }

    [Fact]
    public async Task With_last_years_accreditation_held_here_seeds_from_last_year()
    {
        HeldHere(Year - 1);
        ReExHolds(Year - 1);
        ReExHolds(Year - 2);

        ShouldBeSeededFrom(await Created(await PostSeed()), Year - 1);
    }

    [Fact]
    public async Task A_lapsed_accreditation_seeds_from_the_year_it_was_last_granted()
    {
        HeldHere(Year - 2);
        ReExHolds(Year - 2);

        ShouldBeSeededFrom(await Created(await PostSeed()), Year - 2);
    }

    [Fact]
    public async Task With_several_earlier_accreditations_the_highest_year_wins()
    {
        HeldHere(Year - 4);
        HeldHere(Year - 2);
        HeldHere(Year - 3);
        ReExHolds(Year - 4);
        ReExHolds(Year - 3);
        ReExHolds(Year - 2);

        ShouldBeSeededFrom(await Created(await PostSeed()), Year - 2);
    }

    // Only an Approved application with a number counts as granted, and only for this registration
    // and material; anything else leaves last year as the source, as before.
    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Submitted)]
    public async Task An_earlier_application_that_was_not_granted_is_not_a_source(
        ApplicationStatus status
    )
    {
        HeldHere(Year - 2, status);
        ReExHolds(Year - 1);
        ReExHolds(Year - 2);

        ShouldBeSeededFrom(await Created(await PostSeed()), Year - 1);
    }

    [Fact]
    public async Task An_accreditation_for_another_registration_or_material_is_not_a_source()
    {
        HeldHere(Year - 2, configure: a => a.RegistrationId = "reg-other");
        HeldHere(Year - 3, configure: a => a.MaterialType = MaterialType.Steel);
        ReExHolds(Year - 1);

        ShouldBeSeededFrom(await Created(await PostSeed()), Year - 1);
    }

    [Fact]
    public async Task With_no_accreditation_ever_granted_seeding_fails_as_before()
    {
        var response = await PostSeed();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _factory.FakePersistence.GetByOrganisationAsync(OrganisationId))
            .Should()
            .BeEmpty();
    }

    // ReEx keeps one accreditation per registration, so it may not hold the earlier year any more.
    [Fact]
    public async Task When_ReEx_no_longer_holds_the_latest_granted_year_seeds_from_last_year_as_before()
    {
        HeldHere(Year - 2);
        ReExHolds(Year - 1);

        ShouldBeSeededFrom(await Created(await PostSeed()), Year - 1);
    }

    [Fact]
    public async Task When_ReEx_holds_neither_the_latest_granted_year_nor_last_year_seeding_fails_as_before()
    {
        HeldHere(Year - 2);

        var response = await PostSeed();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _factory.FakePersistence.GetByOrganisationAsync(OrganisationId))
            .Should()
            .ContainSingle("only the earlier approval is held");
    }

    // Only a not-found falls back: a ReEx failure is reported, as before.
    [Fact]
    public async Task When_ReEx_fails_for_the_latest_granted_year_seeding_returns_502()
    {
        HeldHere(Year - 2);
        ReExHolds(Year - 1);
        _factory
            .MockReExAdapter.GetAccreditationAsync(
                OrganisationId,
                RegistrationId,
                MaterialType.Plastic,
                Year - 2
            )
            .Returns(
                Task.FromResult(
                    ReExResult<ReExAccreditationDto>.Fail(
                        new ReExError(ReExErrorKind.ServerError, "ReEx is down"),
                        502
                    )
                )
            );

        var response = await PostSeed();

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }
}
