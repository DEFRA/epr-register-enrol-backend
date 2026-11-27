using System.Net;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AdminApi.OperatorJourney;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Test.Auth;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static EprRegisterEnrolBackend.Test.TestSupport.ProblemAssertions;

namespace EprRegisterEnrolBackend.Test.AdminApi;

// RA-753: the Operator journey search, driven the way the admin service calls it (signed, through the
// real host outside Development) over a real ephemeral Mongo seeded with a small domain model: two
// organisations with a registration each, one with none, and an older version of several records that
// must never show.
public class OperatorJourneySearchEndpointTests : IDisposable
{
    private const string AdminClientId = "epr-register-enrol-admin";
    private const string AdminSecret = "admin-integration-secret";
    private const string ReExClientId = "epr-backend";
    private const string ReExSecret = "reex-integration-secret";
    private const string Base = "/admin/operator-journey";

    private static readonly DateTime T0 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly EphemeralMongoTestFactory _factory;
    private readonly HttpClient _client;
    private readonly World _world;

    public OperatorJourneySearchEndpointTests(MongoIntegrationFixture fixture)
    {
        _factory = new EphemeralMongoTestFactory(
            fixture,
            "admin_oj_search",
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["ServiceClients:Clients:0:Name"] = "reex",
                ["ServiceClients:Clients:0:ClientId"] = ReExClientId,
                ["ServiceClients:Clients:1:Name"] = "admin",
                ["ServiceClients:Clients:1:ClientId"] = AdminClientId,
                ["AUTH_SHARED_SECRET:REEX"] = ReExSecret,
                ["AUTH_SHARED_SECRET:ADMIN"] = AdminSecret,
                ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = "integration-management-be-secret",
                ["AUTH_SHARED_SECRET:FRONTEND"] = "integration-frontend-secret",
                ["CaseWorking:UseStub"] = "true",
            }
        );
        _client = _factory.CreateClient();
        _world = new World();
        _world.SeedAsync(Mongo).GetAwaiter().GetResult();
    }

    // Each test seeds a database of its own on the shared mongod; drop it so they don't pile up there.
    public void Dispose()
    {
        Mongo.GetClient().DropDatabase(_factory.DatabaseName);
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMongoDbClientFactory Mongo => _factory.Services.GetRequiredService<IMongoDbClientFactory>();

    private Task<HttpResponseMessage> Get(string path, string clientId = AdminClientId, string secret = AdminSecret) =>
        _client.SendAsync(ServiceClientSigner.Request(HttpMethod.Get, path, clientId, secret), Ct);

    private async Task<JsonElement> Page(string path)
    {
        var response = await Get(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private async Task<List<string>> Ids(string path) =>
        (await Page(path)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToList();

    public static TheoryData<string> Paths =>
        ["accreditations", "accreditation-applications", "registrations", "organisations", "overseas-sites"];

    // ---- AC01, AC09, AC11: every current record, newest first, a page at a time ----

    [Theory]
    [InlineData("accreditations", 2)]
    [InlineData("accreditation-applications", 2)]
    [InlineData("registrations", 2)]
    [InlineData("organisations", 3)]
    [InlineData("overseas-sites", 2)]
    public async Task WithNoSearchOrFilters_EveryCurrentRecordIsListed_WithTheDefaults(string type, int current)
    {
        var body = await Page($"{Base}/{type}");

        body.GetProperty("pageNumber").GetInt32().Should().Be(1);
        body.GetProperty("limit").GetInt32().Should().Be(50);
        body.GetProperty("totalItems").GetInt32().Should().Be(current);
        body.GetProperty("items").GetArrayLength().Should().Be(current);
    }

    [Fact]
    public async Task OlderVersions_AreNeverSearchedOrListed()
    {
        (await Ids($"{Base}/registrations?q=R24OLD")).Should().BeEmpty();
        (await Ids($"{Base}/accreditations?q=A26OLD")).Should().BeEmpty();
        (await Ids($"{Base}/accreditations?status=Cancelled")).Should().BeEmpty();
        (await Ids($"{Base}/overseas-sites?country=France")).Should().BeEmpty();
        (await Ids($"{Base}/accreditation-applications?status=Withdrawn")).Should().BeEmpty();
    }

    [Fact]
    public async Task OtherApplicationTypes_AreNotAccreditationApplications()
    {
        (await Ids($"{Base}/accreditation-applications")).Should().NotContain(_world.OrsChangeApplicationId.ToString());
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Rows_AreMostRecentlyChangedFirst(string type)
    {
        var items = (await Page($"{Base}/{type}")).GetProperty("items").EnumerateArray().ToList();

        var changed = items.Select(i => i.GetProperty("updatedAt").GetDateTime()).ToList();
        changed.Should().BeInDescendingOrder();
        changed.Distinct().Should().HaveCount(items.Count, "the seeded records were each changed at a different time");
    }

    [Fact]
    public async Task Paging_GivesThePageAskedFor_AndTheTotal_AndAnEmptyPagePastTheEnd()
    {
        var all = await Ids($"{Base}/organisations");

        var second = await Page($"{Base}/organisations?limit=1&pageNumber=2");
        second.GetProperty("totalItems").GetInt32().Should().Be(3);
        second.GetProperty("limit").GetInt32().Should().Be(1);
        second.GetProperty("items")[0].GetProperty("id").GetString().Should().Be(all[1]);

        var past = await Page($"{Base}/organisations?limit=100&pageNumber=2");
        past.GetProperty("totalItems").GetInt32().Should().Be(3);
        past.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // ---- AC09: what each row carries ----

    [Fact]
    public async Task AnAccreditationRow_HasItsNumberStatusYearBandRegistrationAndOrganisation()
    {
        var item = (await Page($"{Base}/accreditations?q={_world.AccreditationA}&searchFields=id"))
            .GetProperty("items")[0];

        item.GetProperty("id").GetString().Should().Be(_world.AccreditationA.ToString());
        item.GetProperty("accreditationNumber").GetString().Should().Be("A27ER5001120018PL");
        item.GetProperty("status").GetString().Should().Be("Approved");
        item.GetProperty("year").GetInt32().Should().Be(2027);
        item.GetProperty("tonnageBand").GetString().Should().Be("up_to_5000");
        item.GetProperty("registrationId").GetString().Should().Be(_world.RegistrationA.ToString());
        item.GetProperty("registrationNumber").GetString().Should().Be("R25ER5001120018PL");
        item.GetProperty("material").GetString().Should().Be("Plastic");
        AssertOrganisation(item, _world.OrganisationA, "Harbourside Plastics Ltd", 500112);
        item.GetProperty("updatedAt").GetDateTime().Should().Be(T0.AddHours(3));
    }

    [Fact]
    public async Task AnApplicationRow_HasItsReferenceStatusYearRegistrationAndOrganisation()
    {
        var items = (await Page($"{Base}/accreditation-applications")).GetProperty("items");
        var submitted = items.EnumerateArray().Single(i => i.GetProperty("id").GetString() == _world.ApplicationA.ToString());

        submitted.GetProperty("applicationReference").GetString().Should().Be("AP27EA500112ABPL");
        submitted.GetProperty("accreditationNumber").GetString().Should().Be("A27ER5001120018PL");
        submitted.GetProperty("status").GetString().Should().Be("Submitted");
        submitted.GetProperty("year").GetInt32().Should().Be(2027);
        submitted.GetProperty("registrationNumber").GetString().Should().Be("R25ER5001120018PL");
        submitted.GetProperty("material").GetString().Should().Be("Plastic");
        submitted.GetProperty("nation").GetString().Should().Be("England");
        AssertOrganisation(submitted, _world.OrganisationA, "Harbourside Plastics Ltd", 500112);

        // A draft has no reference and no registration yet: those are null, its organisation still shows.
        var draft = items.EnumerateArray().Single(i => i.GetProperty("id").GetString() == _world.DraftApplicationB.ToString());
        draft.GetProperty("applicationReference").ValueKind.Should().Be(JsonValueKind.Null);
        draft.GetProperty("registrationId").ValueKind.Should().Be(JsonValueKind.Null);
        draft.GetProperty("material").ValueKind.Should().Be(JsonValueKind.Null);
        draft.GetProperty("nation").ValueKind.Should().Be(JsonValueKind.Null);
        AssertOrganisation(draft, _world.OrganisationB, "Delta Green Recycling", 500390);
    }

    [Fact]
    public async Task ARegistrationRow_HasItsNumberStatusMaterialNationTypeAndOrganisation()
    {
        var item = (await Page($"{Base}/registrations?q=R25ER")).GetProperty("items")[0];

        item.GetProperty("id").GetString().Should().Be(_world.RegistrationA.ToString());
        item.GetProperty("registrationNumber").GetString().Should().Be("R25ER5001120018PL");
        item.GetProperty("status").GetString().Should().Be("Approved");
        item.GetProperty("material").GetString().Should().Be("Plastic");
        item.GetProperty("nation").GetString().Should().Be("England");
        item.GetProperty("wasteProcessingType").GetString().Should().Be("Reprocessor");
        AssertOrganisation(item, _world.OrganisationA, "Harbourside Plastics Ltd", 500112);
    }

    [Fact]
    public async Task AnOrganisationRow_HasItsNameNumberAndRegistrationCount_AndNullsWhereNoneIsHeld()
    {
        var items = (await Page($"{Base}/organisations")).GetProperty("items").EnumerateArray().ToList();

        var harbourside = items.Single(i => i.GetProperty("id").GetString() == _world.OrganisationA.ToString());
        AssertOrganisation(harbourside, _world.OrganisationA, "Harbourside Plastics Ltd", 500112);
        harbourside.GetProperty("registrationCount").GetInt32().Should().Be(1);
        harbourside.TryGetProperty("status", out _).Should().BeFalse("an organisation has no status");

        var empty = items.Single(i => i.GetProperty("id").GetString() == _world.OrganisationC.ToString());
        empty.GetProperty("organisation").GetProperty("number").ValueKind.Should().Be(JsonValueKind.Null);
        empty.GetProperty("registrationCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task AnOverseasSiteRow_HasItsOrsIdNameCountryStatusRegistrationAndOrganisation()
    {
        var item = (await Page($"{Base}/overseas-sites?q=ORS-0001")).GetProperty("items")[0];

        item.GetProperty("orsId").GetString().Should().Be("ORS-0001");
        item.GetProperty("siteName").GetString().Should().Be("Rotterdam Polymer Works");
        item.GetProperty("country").GetString().Should().Be("Netherlands");
        item.GetProperty("status").GetString().Should().Be("Active");
        item.GetProperty("registrationNumber").GetString().Should().Be("R25ER5001120018PL");
        AssertOrganisation(item, _world.OrganisationA, "Harbourside Plastics Ltd", 500112);
    }

    // AC14: rows hold no contact details, addresses, users or submitter, though the stored records do.
    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Rows_HoldNoPersonalDataBeyondTheOrganisationName(string type)
    {
        var raw = await (await Get($"{Base}/{type}")).Content.ReadAsStringAsync(Ct);

        raw.Should().NotContain(World.ContactEmail).And.NotContain(World.ContactName).And.NotContain("Quay Street");
    }

    private static void AssertOrganisation(JsonElement item, ObjectId id, string name, int number)
    {
        var organisation = item.GetProperty("organisation");
        organisation.GetProperty("id").GetString().Should().Be(id.ToString());
        organisation.GetProperty("name").GetString().Should().Be(name);
        organisation.GetProperty("number").GetInt32().Should().Be(number);
    }

    // ---- AC03 to AC06: what q finds ----

    [Theory]
    [InlineData("accreditations", "A27ER5001120018PL")]
    [InlineData("accreditations", "R25ER5001120018PL")]
    [InlineData("accreditations", "500112")]
    [InlineData("accreditation-applications", "AP27EA500112ABPL")]
    [InlineData("accreditation-applications", "A27ER5001120018PL")]
    [InlineData("accreditation-applications", "R25ER5001120018PL")]
    [InlineData("accreditation-applications", "500112")]
    [InlineData("registrations", "R25ER5001120018PL")]
    [InlineData("registrations", "500112")]
    [InlineData("organisations", "500112")]
    [InlineData("overseas-sites", "ORS-0001")]
    [InlineData("overseas-sites", "R25ER5001120018PL")]
    [InlineData("overseas-sites", "500112")]
    public async Task AKnownReferenceOrNumber_FindsOnlyItsRecords(string type, string q)
    {
        var body = await Page($"{Base}/{type}?q={q}");

        body.GetProperty("totalItems").GetInt32().Should().Be(1);
        body.GetProperty("items")[0].GetProperty("organisation").GetProperty("number").GetInt32().Should().Be(500112);
    }

    [Fact]
    public async Task AKnownId_FindsThatRecord_InAnyCase()
    {
        (await Ids($"{Base}/accreditations?q={_world.AccreditationA}")).Should().Equal(_world.AccreditationA.ToString());
        (await Ids($"{Base}/accreditation-applications?q={_world.ApplicationA.ToString().ToUpperInvariant()}"))
            .Should().Equal(_world.ApplicationA.ToString());
        (await Ids($"{Base}/registrations?q={_world.RegistrationB}")).Should().Equal(_world.RegistrationB.ToString());
        (await Ids($"{Base}/organisations?q={_world.OrganisationC}")).Should().Equal(_world.OrganisationC.ToString());
        (await Ids($"{Base}/overseas-sites?q={_world.SiteB}")).Should().Equal(_world.SiteB.ToString());
    }

    [Theory]
    [InlineData("accreditations", "27ER50011")]
    [InlineData("accreditations", "0011")]
    [InlineData("registrations", "25ER500")]
    [InlineData("accreditation-applications", "27ea5001")]
    [InlineData("organisations", "0011")]
    [InlineData("overseas-sites", "ER5001120")]
    public async Task PartOfANumber_FindsEveryRecordWhoseNumberContainsIt(string type, string q)
    {
        var body = await Page($"{Base}/{type}?q={q}");

        body.GetProperty("totalItems").GetInt32().Should().Be(1);
        body.GetProperty("items")[0].GetProperty("organisation").GetProperty("number").GetInt32().Should().Be(500112);
    }

    [Theory]
    [InlineData("accreditations", 1)]
    [InlineData("accreditation-applications", 1)]
    [InlineData("registrations", 1)]
    [InlineData("organisations", 1)]
    [InlineData("overseas-sites", 1)]
    public async Task PartOfAnOrganisationName_InAnyCase_FindsThatOrganisationsRecords(string type, int expected)
    {
        foreach (var q in new[] { "green", "GREEN", "Delta%20Gr", "dElTa" })
        {
            var items = (await Page($"{Base}/{type}?q={q}")).GetProperty("items").EnumerateArray().ToList();

            items.Should().HaveCount(expected, q);
            items.Should().OnlyContain(i => i.GetProperty("organisation").GetProperty("name").GetString() == "Delta Green Recycling");
        }
    }

    [Fact]
    public async Task UpperOrLowerCase_MakesNoDifferenceToANumberMatch()
    {
        (await Ids($"{Base}/accreditations?q=a27er")).Should().Equal(_world.AccreditationA.ToString());
        (await Ids($"{Base}/overseas-sites?q=ors-0002")).Should().Equal(_world.SiteB.ToString());
    }

    [Fact]
    public async Task SearchFields_LimitWhatQSearches()
    {
        (await Ids($"{Base}/accreditations?q=500112&searchFields=organisationName")).Should().BeEmpty();
        (await Ids($"{Base}/accreditations?q=500112&searchFields=organisationNumber")).Should().ContainSingle();
        (await Ids($"{Base}/accreditations?q=500112&searchFields=organisationName&searchFields=organisationNumber"))
            .Should().ContainSingle();
        // An id is only matched exactly, and only when id is searched.
        (await Ids($"{Base}/registrations?q={_world.RegistrationA}&searchFields=registrationNumber")).Should().BeEmpty();
        (await Ids($"{Base}/registrations?q={_world.RegistrationA.ToString()[..10]}&searchFields=id")).Should().BeEmpty();
    }

    [Theory]
    [InlineData(".*")]
    [InlineData("R25ER5001120018PL.")]
    [InlineData("(")]
    [InlineData("[a-z]")]
    [InlineData("\\d")]
    [InlineData("$")]
    public async Task QIsMatchedAsText_NeverAsAPattern(string q)
    {
        var body = await Page($"{Base}/registrations?q={Uri.EscapeDataString(q)}");

        body.GetProperty("totalItems").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task ANameHoldingPatternCharacters_IsFoundByThem()
    {
        (await Ids($"{Base}/organisations?q={Uri.EscapeDataString("(UK) Ltd.")}")).Should().Equal(_world.OrganisationC.ToString());
    }

    [Theory]
    [InlineData("?q=")]
    [InlineData("?q=%20%20")]
    public async Task AnEmptyQ_IsTheSameAsNone(string query)
    {
        (await Page($"{Base}/organisations{query}")).GetProperty("totalItems").GetInt32().Should().Be(3);
    }

    // ---- AC07, AC08: filters ----

    [Theory]
    [InlineData("accreditations?status=Approved", 1)]
    [InlineData("accreditations?status=Approved&status=Suspended", 2)]
    [InlineData("accreditations?year=2026", 1)]
    [InlineData("accreditations?year=2026&year=2027", 2)]
    [InlineData("accreditations?tonnageBand=over_10000", 1)]
    [InlineData("accreditations?tonnageBand=up_to_500", 0)]
    [InlineData("accreditation-applications?status=Submitted&status=Saved", 2)]
    [InlineData("accreditation-applications?year=2027", 1)]
    [InlineData("accreditation-applications?material=Plastic", 1)]
    [InlineData("accreditation-applications?material=Glass", 0)]
    [InlineData("accreditation-applications?nation=England&nation=Scotland", 1)]
    [InlineData("registrations?status=Created", 1)]
    [InlineData("registrations?material=Glass&material=Plastic", 2)]
    [InlineData("registrations?nation=Scotland", 1)]
    [InlineData("registrations?nation=Wales", 0)]
    [InlineData("organisations?nation=Scotland", 1)]
    [InlineData("organisations?nation=England&nation=Scotland", 2)]
    [InlineData("organisations?hasRegistration=true", 2)]
    [InlineData("organisations?hasRegistration=false", 1)]
    [InlineData("organisations?hasRegistration=true&hasRegistration=false", 3)]
    [InlineData("overseas-sites?status=Proposed", 1)]
    [InlineData("overseas-sites?country=Germany&country=Netherlands", 2)]
    [InlineData("overseas-sites?country=germany", 0)]
    public async Task AFilter_MatchesAnyOfItsValues(string pathAndQuery, int expected)
    {
        (await Page($"{Base}/{pathAndQuery}")).GetProperty("totalItems").GetInt32().Should().Be(expected);
    }

    [Theory]
    [InlineData("accreditations?q=500112&status=Suspended", 0)]
    [InlineData("accreditations?q=500&status=Suspended&year=2026", 1)]
    [InlineData("accreditations?q=500&status=Suspended&year=2027", 0)]
    [InlineData("registrations?q=harbourside&nation=England", 1)]
    [InlineData("registrations?q=harbourside&nation=Scotland", 0)]
    [InlineData("organisations?q=delta&hasRegistration=false", 0)]
    [InlineData("overseas-sites?q=500390&country=Germany&status=Proposed", 1)]
    public async Task SearchAndFilters_MustAllMatch(string pathAndQuery, int expected)
    {
        (await Page($"{Base}/{pathAndQuery}")).GetProperty("totalItems").GetInt32().Should().Be(expected);
    }

    // ---- what a direct call could send ----

    [Theory]
    [InlineData("accreditations?limit=101", "invalid_limit")]
    [InlineData("accreditations?limit=0", "invalid_limit")]
    [InlineData("registrations?limit=ten", "invalid_limit")]
    [InlineData("organisations?pageNumber=0", "invalid_page_number")]
    [InlineData("organisations?pageNumber=-1", "invalid_page_number")]
    [InlineData("accreditations?status=approved", "invalid_status")]
    [InlineData("accreditations?status=0", "invalid_status")]
    [InlineData("accreditations?status=", "invalid_status")]
    [InlineData("accreditations?status=Submitted", "invalid_status")]
    [InlineData("accreditation-applications?status=Suspended", "invalid_status")]
    [InlineData("registrations?status=Active", "invalid_status")]
    [InlineData("overseas-sites?status=Approved", "invalid_status")]
    [InlineData("accreditations?year=1999", "invalid_year")]
    [InlineData("accreditations?year=2027.5", "invalid_year")]
    [InlineData("accreditation-applications?year=", "invalid_year")]
    [InlineData("accreditations?tonnageBand=UpTo500", "invalid_tonnage_band")]
    [InlineData("registrations?material=plastic", "invalid_material")]
    [InlineData("accreditation-applications?nation=Ireland", "invalid_nation")]
    [InlineData("organisations?hasRegistration=yes", "invalid_has_registration")]
    [InlineData("organisations?hasRegistration=", "invalid_has_registration")]
    [InlineData("overseas-sites?country=", "invalid_country")]
    [InlineData("overseas-sites?country=Ger%0Amany", "invalid_country")]
    [InlineData("accreditations?searchFields=siteName", "invalid_search_fields")]
    [InlineData("organisations?searchFields=registrationNumber", "invalid_search_fields")]
    [InlineData("registrations?q=a&q=b", "invalid_q")]
    [InlineData("registrations?q=a%0Ab", "invalid_q")]
    [InlineData("accreditations?nation=England", "unknown_parameter")]
    [InlineData("organisations?status=Approved", "unknown_parameter")]
    [InlineData("registrations?Status=Approved", "unknown_parameter")]
    [InlineData("registrations?page=2", "unknown_parameter")]
    public async Task ABadParameter_Is400_WithItsCode(string pathAndQuery, string code)
    {
        var response = await Get($"{Base}/{pathAndQuery}");

        await AssertProblem(response, HttpStatusCode.BadRequest, code);
    }

    [Fact]
    public async Task SearchFieldsWithoutQ_AreStillChecked_ButAKnownOneLimitsNothing()
    {
        var all = await Ids($"{Base}/registrations");

        (await Ids($"{Base}/registrations?searchFields=registrationNumber")).Should().Equal(all).And.HaveCount(2);
        await AssertProblem(
            await Get($"{Base}/registrations?searchFields=siteName"),
            HttpStatusCode.BadRequest,
            "invalid_search_fields"
        );
    }

    // The last page number accepted is int.MaxValue; it is far past what Mongo can skip, so it is an empty page.
    [Theory]
    [InlineData("2147483647", HttpStatusCode.OK)]
    [InlineData("2147483648", HttpStatusCode.BadRequest)]
    public async Task ThePageNumberLimit_IsTheLargestWholeNumber_AndThatPageIsEmpty(
        string pageNumber,
        HttpStatusCode expected
    )
    {
        var path = $"{Base}/registrations?pageNumber={pageNumber}";
        if (expected == HttpStatusCode.BadRequest)
        {
            await AssertProblem(await Get(path), expected, "invalid_page_number");
            return;
        }

        var body = await Page(path);
        body.GetProperty("pageNumber").GetInt32().Should().Be(int.MaxValue);
        body.GetProperty("totalItems").GetInt32().Should().Be(2);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AQOver100Characters_Is400()
    {
        var response = await Get($"{Base}/organisations?q={new string('a', 101)}");

        await AssertProblem(response, HttpStatusCode.BadRequest, "invalid_q");
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task AnotherKnownClient_Is403(string type)
    {
        var response = await Get($"{Base}/{type}", ReExClientId, ReExSecret);

        await AssertProblem(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task AnUnsignedRequest_Is401(string type)
    {
        var response = await _client.GetAsync($"{Base}/{type}", Ct);

        await AssertProblem(response, HttpStatusCode.Unauthorized, "missing_header");
    }

    // ---- broken stored links fail loudly ----

    [Fact]
    public async Task ARecordWhoseOrganisationIsMissing_Is500_AndLoggedAsAnError_NeverLeftOut()
    {
        await Mongo
            .GetCollection<OrganisationEntity>(DomainModelCollections.Organisations)
            .DeleteOneAsync(o => o.Id == _world.OrganisationB, Ct);
        var logs = new CapturingLoggerFactory();
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILoggerFactory>(logs))
        );
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            ServiceClientSigner.Request(HttpMethod.Get, $"{Base}/registrations", AdminClientId, AdminSecret),
            Ct
        );

        await AssertProblem(response, HttpStatusCode.InternalServerError, "invalid_stored_record");
        var error = logs.Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error && e.Message.StartsWith("Admin Operator journey search"))
            .Subject;
        error.Properties["RecordId"].Should().Be(_world.RegistrationB.ToString());
        error.Properties["LinkedId"].Should().Be(_world.OrganisationB.ToString());
    }

    [Theory]
    [InlineData("accreditations")]
    [InlineData("overseas-sites")]
    public async Task ARecordWhoseRegistrationIsMissing_Is500(string type)
    {
        await Mongo
            .GetCollection<RegistrationEntity>(DomainModelCollections.RegistrationVersions)
            .DeleteManyAsync(r => r.EntityId == _world.RegistrationA, Ct);

        await AssertProblem(await Get($"{Base}/{type}"), HttpStatusCode.InternalServerError, "invalid_stored_record");
    }

    [Fact]
    public async Task AnApplicationWhoseOrganisationIsMissing_Is500()
    {
        await Mongo
            .GetCollection<OrganisationEntity>(DomainModelCollections.Organisations)
            .DeleteOneAsync(o => o.Id == _world.OrganisationB, Ct);

        await AssertProblem(
            await Get($"{Base}/accreditation-applications"),
            HttpStatusCode.InternalServerError,
            "invalid_stored_record"
        );
    }

    // ---- query time limit ----

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task ASearchTheDatabaseStopped_Is503_AsAProblem(string type)
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton(sp => new OperatorJourneyStore(
                    TimingOutMongo(),
                    sp.GetRequiredService<IOptions<ServiceClientsConfig>>()
                ))
            )
        );
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            ServiceClientSigner.Request(HttpMethod.Get, $"{Base}/{type}", AdminClientId, AdminSecret),
            Ct
        );

        await AssertProblem(response, HttpStatusCode.ServiceUnavailable, "query_timeout");
    }

    // Mongo stopping a query after its MaxTime, on whichever collection is asked for first.
    private static IMongoDbClientFactory TimingOutMongo()
    {
        // Each collection is built before it is returned: NSubstitute can't configure one substitute inside another's Returns.
        var accreditations = TimingOut<AccreditationEntity>();
        var applications = TimingOut<ApplicationEntity>();
        var registrations = TimingOut<RegistrationEntity>();
        var organisations = TimingOut<OrganisationEntity>();
        var sites = TimingOut<OverseasSiteEntity>();
        var mongo = Substitute.For<IMongoDbClientFactory>();
        mongo.GetCollection<AccreditationEntity>(Arg.Any<string>()).Returns(accreditations);
        mongo.GetCollection<ApplicationEntity>(Arg.Any<string>()).Returns(applications);
        mongo.GetCollection<RegistrationEntity>(Arg.Any<string>()).Returns(registrations);
        mongo.GetCollection<OrganisationEntity>(Arg.Any<string>()).Returns(organisations);
        mongo.GetCollection<OverseasSiteEntity>(Arg.Any<string>()).Returns(sites);
        return mongo;
    }

    private static IMongoCollection<T> TimingOut<T>()
    {
        var collection = Substitute.For<IMongoCollection<T>>();
        collection
            .CountDocumentsAsync(Arg.Any<FilterDefinition<T>>(), Arg.Any<CountOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(TimeoutException());
        collection
            .DistinctAsync(
                Arg.Any<FieldDefinition<T, string?>>(),
                Arg.Any<FilterDefinition<T>>(),
                Arg.Any<DistinctOptions>(),
                Arg.Any<CancellationToken>()
            )
            .ThrowsAsync(TimeoutException());
        return collection;
    }

    private static MongoExecutionTimeoutException TimeoutException() =>
        (MongoExecutionTimeoutException)
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MongoExecutionTimeoutException));

    // ---- the country filter's values ----

    [Fact]
    public async Task OverseasSiteCountries_AreTheCurrentSitesDistinctCountries_AToZ()
    {
        var body = await Page($"{Base}/overseas-site-countries");

        body.GetProperty("items").EnumerateArray().Select(c => c.GetString()).Should().Equal("Germany", "Netherlands");
    }

    [Fact]
    public async Task OverseasSiteCountries_TakesNoParameters()
    {
        await AssertProblem(
            await Get($"{Base}/overseas-site-countries?status=Active"),
            HttpStatusCode.BadRequest,
            "unknown_parameter"
        );
    }

    [Fact]
    public async Task OverseasSiteCountries_IsForTheAdminClientOnly()
    {
        await AssertProblem(
            await Get($"{Base}/overseas-site-countries", ReExClientId, ReExSecret),
            HttpStatusCode.Forbidden,
            "forbidden"
        );
    }

    [Fact]
    public async Task OverseasSiteCountries_TheDatabaseStopped_Is503()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton(sp => new OperatorJourneyStore(
                    TimingOutMongo(),
                    sp.GetRequiredService<IOptions<ServiceClientsConfig>>()
                ))
            )
        );
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            ServiceClientSigner.Request(HttpMethod.Get, $"{Base}/overseas-site-countries", AdminClientId, AdminSecret),
            Ct
        );

        await AssertProblem(response, HttpStatusCode.ServiceUnavailable, "query_timeout");
    }

    // The seeded domain model. Each record is changed at its own time, so the order is known.
    private sealed class World
    {
        public const string ContactName = "Jo Bloggs";
        public const string ContactEmail = "jo.bloggs@example.com";

        public ObjectId OrganisationA { get; } = ObjectId.GenerateNewId();
        public ObjectId OrganisationB { get; } = ObjectId.GenerateNewId();
        public ObjectId OrganisationC { get; } = ObjectId.GenerateNewId();
        public ObjectId RegistrationA { get; } = ObjectId.GenerateNewId();
        public ObjectId RegistrationB { get; } = ObjectId.GenerateNewId();
        public ObjectId AccreditationA { get; } = ObjectId.GenerateNewId();
        public ObjectId AccreditationB { get; } = ObjectId.GenerateNewId();
        public ObjectId ApplicationA { get; } = ObjectId.GenerateNewId();
        public ObjectId DraftApplicationB { get; } = ObjectId.GenerateNewId();
        public ObjectId OrsChangeApplicationId { get; } = ObjectId.GenerateNewId();
        public ObjectId SiteA { get; } = ObjectId.GenerateNewId();
        public ObjectId SiteB { get; } = ObjectId.GenerateNewId();

        private static ContactPerson Contact => new() { FullName = ContactName, Email = ContactEmail };

        public async Task SeedAsync(IMongoDbClientFactory mongo)
        {
            await mongo
                .GetCollection<OrganisationEntity>(DomainModelCollections.Organisations)
                .InsertManyAsync(
                [
                    new OrganisationEntity
                    {
                        Id = OrganisationA,
                        ReExOrganisationId = "reex-org-a",
                        OrganisationNumber = 500112,
                        Name = "Harbourside Plastics Ltd",
                        ContactDetails = Contact,
                        Address = new PostalAddress { Line1 = "1 Quay Street" },
                        UpdatedAt = T0.AddMinutes(1),
                    },
                    new OrganisationEntity
                    {
                        Id = OrganisationB,
                        ReExOrganisationId = "reex-org-b",
                        OrganisationNumber = 500390,
                        Name = "Delta Green Recycling",
                        UpdatedAt = T0.AddMinutes(2),
                    },
                    // No number yet, and pattern characters in its name.
                    new OrganisationEntity
                    {
                        Id = OrganisationC,
                        ReExOrganisationId = "reex-org-c",
                        Name = "Empty Holdings (UK) Ltd.",
                        UpdatedAt = T0.AddMinutes(3),
                    },
                ]);

            await mongo
                .GetCollection<RegistrationEntity>(DomainModelCollections.RegistrationVersions)
                .InsertManyAsync(
                [
                    Registration(RegistrationA, OrganisationA, "R24OLD5001120018PL", RegistrationStatus.Created, MaterialType.Plastic, Nation.England, T0, current: false, version: 1),
                    Registration(RegistrationA, OrganisationA, "R25ER5001120018PL", RegistrationStatus.Approved, MaterialType.Plastic, Nation.England, T0.AddHours(1), current: true, version: 2),
                    Registration(RegistrationB, OrganisationB, "R25SR5003900021GL", RegistrationStatus.Created, MaterialType.Glass, Nation.Scotland, T0.AddHours(2), current: true, version: 1),
                ]);

            await mongo
                .GetCollection<AccreditationEntity>(DomainModelCollections.AccreditationVersions)
                .InsertManyAsync(
                [
                    Accreditation(AccreditationA, RegistrationA, "A27ER5001120018PL", AccreditationStatus.Approved, 2027, TonnageBand.UpTo5000, T0.AddHours(3), current: true, version: 2),
                    Accreditation(AccreditationA, RegistrationA, "A26OLD5001120018PL", AccreditationStatus.Cancelled, 2027, TonnageBand.UpTo500, T0.AddMinutes(30), current: false, version: 1),
                    Accreditation(AccreditationB, RegistrationB, "A26SR5003900021GL", AccreditationStatus.Suspended, 2026, TonnageBand.Over10000, T0.AddHours(4), current: true, version: 1),
                ]);

            await mongo
                .GetCollection<ApplicationEntity>(DomainModelCollections.ApplicationVersions)
                .InsertManyAsync(
                [
                    Application(ApplicationA, RegistrationA, "reex-org-a", "AP27EA500112ABPL", ApplicationStatus.Submitted, 2027, "A27ER5001120018PL", T0.AddHours(5), current: true, version: 2),
                    Application(ApplicationA, RegistrationA, "reex-org-a", null, ApplicationStatus.Withdrawn, 2027, null, T0.AddMinutes(40), current: false, version: 1),
                    Application(DraftApplicationB, null, "reex-org-b", null, ApplicationStatus.Saved, 2026, null, T0.AddHours(6), current: true, version: 1),
                ]);

            // Another application type in the same collection, stored as raw as it would be.
            await mongo
                .GetCollection<BsonDocument>(DomainModelCollections.ApplicationVersions)
                .InsertOneAsync(
                    new BsonDocument
                    {
                        { "entityId", OrsChangeApplicationId },
                        { "version", 1L },
                        { "isCurrent", true },
                        { "validFrom", T0.AddHours(9) },
                        { "applicationType", "OrsChange" },
                        { "reExOrganisationId", "reex-org-a" },
                        { "status", "Submitted" },
                    }
                );

            await mongo
                .GetCollection<OverseasSiteEntity>(DomainModelCollections.OverseasSiteVersions)
                .InsertManyAsync(
                [
                    Site(SiteA, RegistrationA, "ORS-0001", "Rotterdam Polymer Works", "Netherlands", OverseasSiteStatus.Active, T0.AddHours(7), current: true, version: 2),
                    Site(SiteA, RegistrationA, "ORS-0001", "Rotterdam Polymer Works", "France", OverseasSiteStatus.Proposed, T0.AddMinutes(50), current: false, version: 1),
                    Site(SiteB, RegistrationB, "ORS-0002", "Hamburg Glashütte", "Germany", OverseasSiteStatus.Proposed, T0.AddHours(8), current: true, version: 1),
                ]);
        }

        private static RegistrationEntity Registration(
            ObjectId entityId, ObjectId organisationId, string number, RegistrationStatus status,
            MaterialType material, Nation nation, DateTime validFrom, bool current, long version
        ) =>
            new()
            {
                EntityId = entityId,
                Version = version,
                IsCurrent = current,
                ValidFrom = validFrom,
                OrganisationId = organisationId,
                RegistrationNumber = number,
                Status = status,
                Material = material,
                Nation = nation,
                WasteProcessingType = WasteProcessingKind.Reprocessor,
                ContactDetails = Contact,
            };

        private static AccreditationEntity Accreditation(
            ObjectId entityId, ObjectId registrationId, string number, AccreditationStatus status, int year,
            TonnageBand band, DateTime validFrom, bool current, long version
        ) =>
            new()
            {
                EntityId = entityId,
                Version = version,
                IsCurrent = current,
                ValidFrom = validFrom,
                RegistrationId = registrationId,
                AccreditationNumber = number,
                Status = status,
                Year = year,
                TonnageBand = band,
            };

        private static ApplicationEntity Application(
            ObjectId entityId, ObjectId? registrationId, string reExOrganisationId, string? reference,
            ApplicationStatus status, int year, string? reservedNumber, DateTime validFrom, bool current, long version
        ) =>
            new()
            {
                EntityId = entityId,
                Version = version,
                IsCurrent = current,
                ValidFrom = validFrom,
                RegistrationId = registrationId,
                ReExOrganisationId = reExOrganisationId,
                ApplicationReference = reference,
                Status = status,
                SubmittedBy = new Submitter { FullName = ContactName, JobTitle = "Director", Email = ContactEmail },
                Details = new AccreditationDetails
                {
                    Kind = AccreditationKind.New,
                    Year = year,
                    ReservedAccreditationNumber = reservedNumber,
                },
            };

        private static OverseasSiteEntity Site(
            ObjectId entityId, ObjectId registrationId, string orsId, string name, string country,
            OverseasSiteStatus status, DateTime validFrom, bool current, long version
        ) =>
            new()
            {
                EntityId = entityId,
                Version = version,
                IsCurrent = current,
                ValidFrom = validFrom,
                RegistrationId = registrationId,
                OrsId = orsId,
                SiteName = name,
                Country = country,
                Status = status,
                Contact = Contact,
            };
    }
}
