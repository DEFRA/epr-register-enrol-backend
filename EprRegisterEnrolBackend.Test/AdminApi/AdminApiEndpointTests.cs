using System.Net;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Test.Auth;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using static EprRegisterEnrolBackend.Test.TestSupport.ProblemAssertions;

namespace EprRegisterEnrolBackend.Test.AdminApi;

// RA-740: drives the admin API the way the admin service does: through the real host and auth
// pipeline (outside Development), with signed requests, against a real ephemeral Mongo. The search
// itself is covered in LegacyApplicationSearchTests; this pins the HTTP contract.
public class AdminApiEndpointTests : IDisposable
{
    private const string AdminClientId = "epr-register-enrol-admin";
    private const string AdminSecret = "admin-integration-secret";
    private const string ReExClientId = "epr-backend";
    private const string ReExSecret = "reex-integration-secret";
    private const string SearchPath = "/admin/accreditation-applications";

    private readonly EphemeralMongoTestFactory _factory;
    private readonly HttpClient _client;

    public AdminApiEndpointTests(MongoIntegrationFixture fixture)
    {
        _factory = new EphemeralMongoTestFactory(
            fixture,
            "admin_api",
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
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<AccreditationApplicationModel> Seed(
        Action<AccreditationApplicationModel>? configure = null
    )
    {
        var application = new AccreditationApplicationModel
        {
            OrganisationId = "reex-object-id",
            OrgId = 500112,
            OrganisationName = "Harbourside Plastics Ltd",
            Year = 2027,
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = ApplicationStatus.Submitted,
            RegistrationReference = "R25ER5001120018PL",
            AccreditationReference = "A27ER5001120018PL",
            WasteProcessingType = "reprocessor",
            Nation = Nation.England,
            UpdatedAt = DateTime.UtcNow,
        };
        configure?.Invoke(application);
        var created = await _factory
            .Services.GetRequiredService<IAccreditationApplicationPersistence>()
            .CreateAsync(application);
        created.Should().NotBeNull();
        return created!;
    }

    private Task<HttpResponseMessage> Get(
        string path,
        string clientId = AdminClientId,
        string secret = AdminSecret
    ) => _client.SendAsync(ServiceClientSigner.Request(HttpMethod.Get, path, clientId, secret), Ct);

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();


    // ---- the search ----

    [Fact]
    public async Task TheAdminClient_GetsAPage_WithTheDefaults()
    {
        await Seed();

        var response = await Get(SearchPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var body = await Json(response);
        body.GetProperty("pageNumber").GetInt32().Should().Be(1);
        body.GetProperty("limit").GetInt32().Should().Be(50);
        body.GetProperty("totalItems").GetInt32().Should().Be(1);
        body.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task EachItem_IsTheStoredApplication_WithItsIdAndDomainModelLinks()
    {
        var registrationEntityId = ObjectId.GenerateNewId();
        var accreditationEntityId = ObjectId.GenerateNewId();
        var workItem = Guid.NewGuid();
        var application = await Seed(a =>
        {
            a.RegistrationEntityId = registrationEntityId;
            a.AccreditationEntityId = accreditationEntityId;
            a.CaseManagementWorkItemId = workItem;
            a.SubmittedBy = new SubmittedByModel { FullName = "Jo Bloggs", JobTitle = "Director" };
        });

        var item = (await Json(await Get(SearchPath))).GetProperty("items")[0];

        item.GetProperty("applicationId").GetString().Should().Be(application.Id.ToString());
        item.GetProperty("registrationEntityId").GetString().Should().Be(registrationEntityId.ToString());
        item.GetProperty("accreditationEntityId").GetString().Should().Be(accreditationEntityId.ToString());
        item.GetProperty("organisationId").GetString().Should().Be("reex-object-id");
        item.GetProperty("orgId").GetInt32().Should().Be(500112);
        item.GetProperty("organisationName").GetString().Should().Be("Harbourside Plastics Ltd");
        item.GetProperty("year").GetInt32().Should().Be(2027);
        item.GetProperty("materialType").GetString().Should().Be("Plastic");
        item.GetProperty("applicationStatus").GetString().Should().Be("Submitted");
        item.GetProperty("nation").GetString().Should().Be("England");
        item.GetProperty("registrationReference").GetString().Should().Be("R25ER5001120018PL");
        item.GetProperty("accreditationReference").GetString().Should().Be("A27ER5001120018PL");
        item.GetProperty("caseManagementWorkItemId").GetString().Should().Be(workItem.ToString());
        item.GetProperty("submittedBy").GetProperty("fullName").GetString().Should().Be("Jo Bloggs");
        item.TryGetProperty("updatedAt", out _).Should().BeTrue();
        item.TryGetProperty("version", out _).Should().BeTrue();
        item.TryGetProperty("prns", out _).Should().BeTrue();
        item.TryGetProperty("businessPlan", out _).Should().BeTrue();
        item.TryGetProperty("samplingPlan", out _).Should().BeTrue();

        // Derived when an operator reads an application, never stored, so never returned.
        item.TryGetProperty("notificationStatus", out _).Should().BeFalse();
        item.TryGetProperty("dueDate", out _).Should().BeFalse();
        item.TryGetProperty("id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task UnlinkedDomainModelIds_AreNull()
    {
        await Seed();

        var item = (await Json(await Get(SearchPath))).GetProperty("items")[0];

        item.GetProperty("registrationEntityId").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("accreditationEntityId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheQueryParameters_ReachTheSearch()
    {
        var match = await Seed(a =>
        {
            a.OrganisationName = "Acme (UK) Ltd";
            a.ApplicationStatus = ApplicationStatus.Approved;
            a.Year = 2026;
        });
        await Seed(a =>
        {
            a.OrganisationName = "Acme (UK) Ltd";
            a.ApplicationStatus = ApplicationStatus.Approved;
            a.Year = 2027;
        });
        await Seed(a =>
        {
            a.OrganisationName = "Acme (UK) Ltd";
            a.ApplicationStatus = ApplicationStatus.Rejected;
            a.Year = 2026;
        });
        // Matches "acme (uk)" only by registration number, which is not searched.
        await Seed(a =>
        {
            a.OrganisationName = "Other";
            a.RegistrationReference = "acme (uk)";
            a.ApplicationStatus = ApplicationStatus.Approved;
            a.Year = 2026;
        });

        var body = await Json(
            await Get(
                $"{SearchPath}?q=%20acme%20(uk)%20&searchFields=organisationName&searchFields=accreditationNumber&status=Approved&status=Withdrawn&year=2026"
            )
        );

        body.GetProperty("totalItems").GetInt32().Should().Be(1);
        body.GetProperty("items")[0]
            .GetProperty("applicationId")
            .GetString()
            .Should()
            .Be(match.Id.ToString());
    }

    [Fact]
    public async Task Paging_ReturnsTheRequestedPage_MostRecentlyUpdatedFirst()
    {
        var now = DateTime.UtcNow;
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var minutes = i;
            ids.Add((await Seed(a => a.UpdatedAt = now.AddMinutes(-minutes))).Id.ToString()!);
        }

        var body = await Json(await Get($"{SearchPath}?pageNumber=2&limit=2"));

        body.GetProperty("pageNumber").GetInt32().Should().Be(2);
        body.GetProperty("limit").GetInt32().Should().Be(2);
        body.GetProperty("totalItems").GetInt32().Should().Be(5);
        body.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("applicationId").GetString())
            .Should()
            .Equal(ids[2], ids[3]);
    }

    [Fact]
    public async Task NoMatches_IsAnEmptyPage()
    {
        await Seed();

        var response = await Get($"{SearchPath}?q=nothing-like-this");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("totalItems").GetInt32().Should().Be(0);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // AC02, AC03, AC05: a whole number, typed in full, finds its application.
    [Theory]
    [InlineData("A27ER5001120018PL", "accreditationNumber")]
    [InlineData("R25ER5001120018PL", "registrationNumber")]
    [InlineData("500112", "organisationNumber")]
    public async Task AWholeNumber_FindsItsApplication_InThatFieldOrAll(string number, string field)
    {
        var match = await Seed();
        await Seed(a =>
        {
            a.OrgId = 600212;
            a.OrganisationName = "Inland Metals Ltd";
            a.RegistrationReference = "R25ER6002120018ST";
            a.AccreditationReference = "A27ER6002120018ST";
        });

        foreach (var query in new[] { $"q={number}&searchFields={field}", $"q={number}" })
        {
            var body = await Json(await Get($"{SearchPath}?{query}"));

            body.GetProperty("totalItems").GetInt32().Should().Be(1, query);
            body.GetProperty("items")[0]
                .GetProperty("applicationId")
                .GetString()
                .Should()
                .Be(match.Id.ToString(), query);
        }
    }

    // AC13: 50 a page by default, and the total counts every match.
    [Fact]
    public async Task MoreThan50Matches_Are50APage_WithTheTotal()
    {
        for (var i = 0; i < 51; i++)
            await Seed();

        var page1 = await Json(await Get(SearchPath));
        var page2 = await Json(await Get($"{SearchPath}?pageNumber=2"));

        page1.GetProperty("limit").GetInt32().Should().Be(50);
        page1.GetProperty("totalItems").GetInt32().Should().Be(51);
        page1.GetProperty("items").GetArrayLength().Should().Be(50);
        page2.GetProperty("totalItems").GetInt32().Should().Be(51);
        page2.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    // A draft (Saved or Started) has no references yet, and an application that has never been
    // submitted can have no organisation number. Every one is still listed, with those fields null.
    [Fact]
    public async Task Drafts_WithNoReferencesOrOrganisationNumber_AreListed()
    {
        await Seed();
        foreach (var status in new[] { ApplicationStatus.Saved, ApplicationStatus.Started })
            await Seed(a =>
            {
                a.ApplicationStatus = status;
                a.AccreditationReference = null;
                a.RegistrationReference = null;
                a.OrgId = null;
            });

        var body = await Json(await Get(SearchPath));

        body.GetProperty("totalItems").GetInt32().Should().Be(3);
        var drafts = body.GetProperty("items")
            .EnumerateArray()
            .Where(item => item.GetProperty("applicationStatus").GetString() is "Saved" or "Started")
            .ToList();
        drafts.Should().HaveCount(2);
        foreach (var draft in drafts)
        {
            draft.GetProperty("accreditationReference").ValueKind.Should().Be(JsonValueKind.Null);
            draft.GetProperty("registrationReference").ValueKind.Should().Be(JsonValueKind.Null);
            draft.GetProperty("orgId").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    // ---- 400 ----

    public static TheoryData<string, string> InvalidQueries() =>
        new()
        {
            { $"q={new string('a', 101)}", "invalid_q" },
            { "q=a&q=b", "invalid_q" },
            { "q=a%00b", "invalid_q" },
            { "q=a%0Ab", "invalid_q" },
            { "searchFields=postcode", "invalid_search_fields" },
            { "searchFields=OrganisationName", "invalid_search_fields" },
            { "searchFields=", "invalid_search_fields" },
            { "status=approved", "invalid_status" },
            { "status=Approved&status=Granted", "invalid_status" },
            { "status=7", "invalid_status" },
            { "year=1999", "invalid_year" },
            { "year=twenty", "invalid_year" },
            { "year=", "invalid_year" },
            { "pageNumber=0", "invalid_page_number" },
            { "pageNumber=-1", "invalid_page_number" },
            { "pageNumber=1.5", "invalid_page_number" },
            { "limit=0", "invalid_limit" },
            { "limit=101", "invalid_limit" },
            { "limit=ten", "invalid_limit" },
        };

    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public async Task AnInvalidQuery_Is400_WithItsCode(string queryString, string code)
    {
        var response = await Get($"{SearchPath}?{queryString}");

        await AssertProblem(response, HttpStatusCode.BadRequest, code);
    }

    [Fact]
    public async Task AQOf100Characters_AfterTrimming_IsValid()
    {
        var response = await Get($"{SearchPath}?q=%20%20{new string('a', 100)}%20%20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- who may call it ----

    [Fact]
    public async Task AnotherKnownClient_Is403()
    {
        await Seed();

        var response = await Get(SearchPath, ReExClientId, ReExSecret);

        await AssertProblem(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task AnUnsignedRequest_Is401()
    {
        var response = await _client.GetAsync(SearchPath, Ct);

        await AssertProblem(response, HttpStatusCode.Unauthorized, "missing_header");
        response.Headers.WwwAuthenticate.ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TheAdminClientWithTheWrongSecret_Is401()
    {
        var response = await Get(SearchPath, secret: "not-the-secret");

        await AssertProblem(response, HttpStatusCode.Unauthorized, "invalid_signature");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task WritingToTheRoute_Is405_AsAProblem(string method)
    {
        var response = await _client.SendAsync(
            ServiceClientSigner.Request(new HttpMethod(method), SearchPath, AdminClientId, AdminSecret),
            Ct
        );

        await AssertProblem(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
        response.Content.Headers.Allow.Should().Contain("GET");
    }

    [Fact]
    public async Task TheAdminClientNamedWithAnotherCase_GetsAPage()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder
                .UseSetting("ServiceClients:Clients:1:Name", "Admin")
                .ConfigureAppConfiguration(
                    (_, config) =>
                        config.AddInMemoryCollection(
                            new Dictionary<string, string?> { ["ServiceClients:Clients:1:Name"] = "Admin" }
                        )
                )
        );
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            ServiceClientSigner.Request(HttpMethod.Get, SearchPath, AdminClientId, AdminSecret),
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- protection shared with /v1 ----

    [Fact]
    public async Task AClientOverItsLimit_Gets429_AsAProblem()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Configure<ServiceClientsConfig>(config => config.RateLimitPermitsPerMinute = 1)
            )
        );
        using var client = factory.CreateClient();
        Task<HttpResponseMessage> Search() =>
            client.SendAsync(
                ServiceClientSigner.Request(HttpMethod.Get, SearchPath, AdminClientId, AdminSecret),
                Ct
            );

        (await Search()).StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await Search();

        await AssertProblem(response, HttpStatusCode.TooManyRequests, "rate_limited");
    }

    [Fact]
    public async Task ASearchTheDatabaseStopped_Is503_AsAProblem()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<ILegacyApplicationSearch, TimedOutSearch>()
            )
        );
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            ServiceClientSigner.Request(HttpMethod.Get, $"{SearchPath}?q=harbour", AdminClientId, AdminSecret),
            Ct
        );

        await AssertProblem(response, HttpStatusCode.ServiceUnavailable, "query_timeout");
    }

    // Mongo stopping a query after its MaxTime.
    private sealed class TimedOutSearch : ILegacyApplicationSearch
    {
        public Task<(IReadOnlyList<AccreditationApplicationModel> Items, long Total)> SearchLegacyApplicationsAsync(
            LegacyApplicationSearchCriteria search,
            long skip,
            int limit,
            TimeSpan? maxTime = null,
            CancellationToken cancellationToken = default
        ) =>
            throw (MongoExecutionTimeoutException)
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    typeof(MongoExecutionTimeoutException)
                );
    }

    // ---- RA-750: one application, as stored ----

    private IMongoCollection<BsonDocument> RawApplications() =>
        _factory
            .Services.GetRequiredService<EprRegisterEnrolBackend.Utils.Mongo.IMongoDbClientFactory>()
            .GetCollection<BsonDocument>(DomainModelCollections.LegacyApplications);

    [Fact]
    public async Task AnApplication_IsFoundByItsReference_AsStored()
    {
        var created = await Seed(a => a.ApplicationReference = "AP27EA500112ABPL");

        var response = await Get($"{SearchPath}/AP27EA500112ABPL");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var body = await Json(response);
        body.GetProperty("_id").GetProperty("$oid").GetString().Should().Be(created.Id.ToString());
        body.GetProperty("applicationReference").GetString().Should().Be("AP27EA500112ABPL");
        body.GetProperty("organisationName").GetString().Should().Be("Harbourside Plastics Ltd");
        body.GetProperty("updatedAt").GetProperty("$date").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task ADraftWithNoReference_IsFoundByItsId()
    {
        var created = await Seed(a =>
        {
            a.ApplicationStatus = ApplicationStatus.Saved;
            a.AccreditationReference = null;
        });

        var response = await Get($"{SearchPath}/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("_id").GetProperty("$oid").GetString().Should().Be(created.Id.ToString());
        body.TryGetProperty("applicationReference", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TheDocument_KeepsFieldsTheModelDoesNotKnow_AndEnumsStoredAsNumbers()
    {
        var created = await Seed(a => a.ApplicationReference = "AP27EA500112ACPL");
        await RawApplications().UpdateOneAsync(
            new BsonDocument("_id", created.Id),
            new BsonDocument("$set", new BsonDocument { { "retiredField", "kept" }, { "emptyText", "" } }),
            cancellationToken: Ct
        );

        var body = await Json(await Get($"{SearchPath}/AP27EA500112ACPL"));

        body.GetProperty("retiredField").GetString().Should().Be("kept");
        body.GetProperty("emptyText").GetString().Should().BeEmpty();
        body.GetProperty("prns").GetProperty("sectionStatus").ValueKind.Should().Be(JsonValueKind.Number);
        body.GetProperty("applicationStatus").GetString().Should().Be("Submitted");
    }

    [Theory]
    [InlineData("AP27EA599999ZZPL")]
    [InlineData("6716a3c09e4b2d1f8a5c7e30")]
    public async Task AnUnknownReferenceOrId_Is404_AsAProblem(string reference)
    {
        await Seed(a => a.ApplicationReference = "AP27EA500112ADPL");

        var response = await Get($"{SearchPath}/{reference}");

        await AssertProblem(response, HttpStatusCode.NotFound, "application_not_found");
    }

    // The numbers each enum is stored as, as the spec lists them. The Admin UI keeps a copy of these
    // to show their names, so a reorder must fail here, not show the wrong name there.
    [Theory]
    [InlineData(SectionStatus.NotStarted, 0)]
    [InlineData(SectionStatus.InProgress, 1)]
    [InlineData(SectionStatus.Completed, 2)]
    [InlineData(SectionStatus.Submitted, 3)]
    [InlineData(SectionStatus.Queried, 4)]
    public async Task SectionStatus_IsStoredAsTheNumberTheSpecLists(SectionStatus status, int stored)
    {
        await Seed(a =>
        {
            a.ApplicationReference = "AP27EA500112AFPL";
            a.Prns.SectionStatus = status;
        });

        var body = await Json(await Get($"{SearchPath}/AP27EA500112AFPL"));

        body.GetProperty("prns").GetProperty("sectionStatus").GetInt32().Should().Be(stored);
    }

    [Theory]
    [InlineData(PlannedTonnageBand.UpTo500, 0)]
    [InlineData(PlannedTonnageBand.UpTo5000, 1)]
    [InlineData(PlannedTonnageBand.UpTo10000, 2)]
    [InlineData(PlannedTonnageBand.Over10000, 3)]
    public async Task PlannedTonnageBand_IsStoredAsTheNumberTheSpecLists(PlannedTonnageBand band, int stored)
    {
        await Seed(a =>
        {
            a.ApplicationReference = "AP27EA500112AGPL";
            a.Prns.PlannedTonnageBand = band;
        });

        var body = await Json(await Get($"{SearchPath}/AP27EA500112AGPL"));

        body.GetProperty("prns").GetProperty("plannedTonnageBand").GetInt32().Should().Be(stored);
    }

    [Theory]
    [InlineData(FileScanStatus.Pending, AccreditationFileDocumentType.SamplingPlan, 0, 0)]
    [InlineData(FileScanStatus.Clean, AccreditationFileDocumentType.SupportingEvidence, 1, 1)]
    [InlineData(FileScanStatus.Infected, AccreditationFileDocumentType.SamplingPlan, 2, 0)]
    public async Task AFilesScanStatusAndDocumentType_AreStoredAsTheNumbersTheSpecLists(
        FileScanStatus scanStatus,
        AccreditationFileDocumentType documentType,
        int storedScanStatus,
        int storedDocumentType
    )
    {
        await Seed(a =>
        {
            a.ApplicationReference = "AP27EA500112AHPL";
            a.SamplingPlan.Files.Add(
                new AccreditationApplicationFile
                {
                    FileId = "file-1",
                    Filename = "plan.pdf",
                    ContentType = "application/pdf",
                    UploadedByUserId = "user-1",
                    S3Key = "plans/file-1",
                    ScanStatus = scanStatus,
                    DocumentType = documentType,
                }
            );
        });

        var body = await Json(await Get($"{SearchPath}/AP27EA500112AHPL"));

        var file = body.GetProperty("samplingPlan").GetProperty("files")[0];
        file.GetProperty("scanStatus").GetInt32().Should().Be(storedScanStatus);
        file.GetProperty("documentType").GetInt32().Should().Be(storedDocumentType);
    }

    [Fact]
    public async Task APaddedReference_IsStillFound()
    {
        await Seed(a => a.ApplicationReference = "AP27EA500112AJPL");

        var response = await Get($"{SearchPath}/%20AP27EA500112AJPL%20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("applicationReference").GetString().Should().Be("AP27EA500112AJPL");
    }

    [Fact]
    public async Task AnEmptyReference_Is400()
    {
        var response = await Get($"{SearchPath}/%20");

        await AssertProblem(response, HttpStatusCode.BadRequest, "invalid_reference");
    }

    [Fact]
    public async Task AReferenceOver100Characters_Is400()
    {
        var response = await Get($"{SearchPath}/{new string('A', 101)}");

        await AssertProblem(response, HttpStatusCode.BadRequest, "invalid_reference");
    }

    [Fact]
    public async Task AReferenceWithAControlCharacter_Is400()
    {
        var response = await Get($"{SearchPath}/AP27%01");

        await AssertProblem(response, HttpStatusCode.BadRequest, "invalid_reference");
    }

    [Fact]
    public async Task AnotherKnownClient_CannotReadAnApplication()
    {
        await Seed(a => a.ApplicationReference = "AP27EA500112AEPL");

        var response = await Get($"{SearchPath}/AP27EA500112AEPL", ReExClientId, ReExSecret);

        await AssertProblem(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task AReadTheDatabaseStopped_Is503_AsAProblem()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<ILegacyApplicationRead, TimedOutRead>()
            )
        );
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            ServiceClientSigner.Request(HttpMethod.Get, $"{SearchPath}/AP27EA500112AFPL", AdminClientId, AdminSecret),
            Ct
        );

        await AssertProblem(response, HttpStatusCode.ServiceUnavailable, "query_timeout");
    }

    private sealed class TimedOutRead : ILegacyApplicationRead
    {
        public Task<BsonDocument?> GetLegacyApplicationAsync(
            string reference,
            TimeSpan? maxTime = null,
            CancellationToken cancellationToken = default
        ) => throw TimedOut();

        public Task<IReadOnlyList<LegacyApplicationStatus>> GetLegacyApplicationStatusesAsync(
            IReadOnlyCollection<Guid> workItemIds,
            TimeSpan? maxTime = null,
            CancellationToken cancellationToken = default
        ) => throw TimedOut();

        private static MongoExecutionTimeoutException TimedOut() =>
            (MongoExecutionTimeoutException)
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    typeof(MongoExecutionTimeoutException)
                );
    }

    // ---- RA-751: the Legacy status of each of a page of work items ----

    private const string StatusesPath = "/admin/accreditation-application-statuses";
    private const string MappingPath = "/admin/case-management-status-mapping";

    private static string StatusesQuery(params Guid[] workItemIds) =>
        $"{StatusesPath}?{string.Join("&", workItemIds.Select(id => $"workItemId={id}"))}";

    [Fact]
    public async Task EachWorkItem_IsListedInTheOrderAsked_WithItsLinkedApplicationsStatus_OrAsNotFound()
    {
        var updated = await Seed(a =>
        {
            a.CaseManagementWorkItemId = Guid.NewGuid();
            a.ApplicationStatus = ApplicationStatus.Updated;
        });
        var queried = await Seed(a =>
        {
            a.CaseManagementWorkItemId = Guid.NewGuid();
            a.ApplicationStatus = ApplicationStatus.Queried;
            a.RegistrationReference = "R25ER5001120019PL";
        });
        await Seed(a => a.RegistrationReference = "R25ER5001120020PL"); // linked to no work item
        var notInLegacy = Guid.NewGuid();

        var response = await Get(
            StatusesQuery(queried.CaseManagementWorkItemId!.Value, notInLegacy, updated.CaseManagementWorkItemId!.Value)
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await Json(response)).GetProperty("items");
        items.GetArrayLength().Should().Be(3);

        items[0].GetProperty("workItemId").GetString().Should().Be(queried.CaseManagementWorkItemId.ToString());
        items[0].GetProperty("found").GetBoolean().Should().BeTrue();
        items[0].GetProperty("applicationId").GetString().Should().Be(queried.Id.ToString());
        items[0].GetProperty("applicationStatus").GetString().Should().Be("Queried");

        items[1].GetProperty("workItemId").GetString().Should().Be(notInLegacy.ToString());
        items[1].GetProperty("found").GetBoolean().Should().BeFalse();
        items[1].GetProperty("applicationId").ValueKind.Should().Be(JsonValueKind.Null);
        items[1].GetProperty("applicationStatus").ValueKind.Should().Be(JsonValueKind.Null);

        items[2].GetProperty("workItemId").GetString().Should().Be(updated.CaseManagementWorkItemId.ToString());
        items[2].GetProperty("applicationStatus").GetString().Should().Be("Updated");
    }

    [Fact]
    public async Task NoWorkItemFound_IsEveryItemNotFound_NotAnError()
    {
        var response = await Get(StatusesQuery(Guid.NewGuid(), Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await Json(response)).GetProperty("items");
        items.EnumerateArray().Select(i => i.GetProperty("found").GetBoolean()).Should().Equal(false, false);
    }

    [Fact]
    public async Task EachItem_HoldsTheIdsAndStatusOnly_NoPersonalData()
    {
        var workItem = Guid.NewGuid();
        await Seed(a =>
        {
            a.CaseManagementWorkItemId = workItem;
            a.SubmittedBy = new SubmittedByModel { FullName = "Jo Bloggs", JobTitle = "Director" };
        });

        var response = await Get(StatusesQuery(workItem));

        var body = await response.Content.ReadAsStringAsync(Ct);
        body.Should().NotContain("Jo Bloggs").And.NotContain("Harbourside");
        var item = (await Json(response)).GetProperty("items")[0];
        item.EnumerateObject()
            .Select(p => p.Name)
            .Should()
            .BeEquivalentTo("workItemId", "found", "applicationId", "applicationStatus");
    }

    [Fact]
    public async Task OneResultsPageOf50WorkItems_IsAnswered()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();

        var response = await Get(StatusesQuery(ids));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(response)).GetProperty("items").GetArrayLength().Should().Be(50);
    }

    [Fact]
    public async Task AnUppercaseWorkItemId_FindsItsApplication()
    {
        var workItem = Guid.NewGuid();
        await Seed(a => a.CaseManagementWorkItemId = workItem);

        var item = (await Json(await Get($"{StatusesPath}?workItemId={workItem.ToString().ToUpperInvariant()}")))
            .GetProperty("items")[0];

        item.GetProperty("workItemId").GetString().Should().Be(workItem.ToString());
        item.GetProperty("found").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TheLookup_UsesTheIndexOnTheWorkItemLink()
    {
        await Seed(a => a.CaseManagementWorkItemId = Guid.NewGuid());
        var filter = new BsonDocument(
            "caseManagementWorkItemId",
            new BsonDocument(
                "$in",
                new BsonArray { new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard) }
            )
        );

        var explain = await RawApplications()
            .Database.RunCommandAsync<BsonDocument>(
                new BsonDocument
                {
                    {
                        "explain",
                        new BsonDocument
                        {
                            { "find", DomainModelCollections.LegacyApplications },
                            { "filter", filter },
                        }
                    },
                    { "verbosity", "queryPlanner" },
                },
                cancellationToken: Ct
            );

        var plan = explain["queryPlanner"]["winningPlan"].ToJson();
        plan.Should().Contain("IXSCAN").And.Contain("caseManagementWorkItemId");
    }

    public static TheoryData<string, string> InvalidWorkItemIds()
    {
        var id = Guid.NewGuid();
        return new()
        {
            { "", "workItemId is required" },
            { "other=1", "workItemId is required" },
            { "workItemId=", "a GUID" },
            { "workItemId", "a GUID" },
            { "workItemId=not-a-guid", "a GUID" },
            { $"workItemId={id:N}", "a GUID" },
            { $"workItemId={id:B}", "a GUID" },
            { $"workItemId={id}%20", "a GUID" },
            { $"workItemId={id},{Guid.NewGuid()}", "a GUID" },
            { $"workItemId={id}&workItemId={id}", "more than once" },
            { $"workItemId={id}&workItemId={id.ToString().ToUpperInvariant()}", "more than once" },
            {
                string.Join("&", Enumerable.Range(0, 51).Select(_ => $"workItemId={Guid.NewGuid()}")),
                "at most 50"
            },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidWorkItemIds))]
    public async Task AMissingMalformedDuplicateOrTooManyWorkItemIds_Is400(string queryString, string detail)
    {
        var response = await Get($"{StatusesPath}?{queryString}");

        await AssertProblem(response, HttpStatusCode.BadRequest, "invalid_work_item_id");
        (await Json(response)).GetProperty("detail").GetString().Should().Contain(detail);
    }

    public static TheoryData<string> UnreadableStatuses() => new() { "unset", "Granted", "99" };

    [Theory]
    [MemberData(nameof(UnreadableStatuses))]
    public async Task ALinkedApplicationWithNoReadableStatus_Is500_AndLoggedAsAnError_NeverAMadeUpStatus(
        string stored
    )
    {
        var workItem = Guid.NewGuid();
        var application = await Seed(a => a.CaseManagementWorkItemId = workItem);
        var update = stored switch
        {
            "unset" => new BsonDocument("$unset", new BsonDocument("applicationStatus", "")),
            "99" => new BsonDocument("$set", new BsonDocument("applicationStatus", 99)),
            _ => new BsonDocument("$set", new BsonDocument("applicationStatus", stored)),
        };
        await RawApplications().UpdateOneAsync(new BsonDocument("_id", application.Id), update, cancellationToken: Ct);
        var logs = new CapturingLoggerFactory();
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILoggerFactory>(logs))
        );
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            ServiceClientSigner.Request(HttpMethod.Get, StatusesQuery(workItem), AdminClientId, AdminSecret),
            Ct
        );

        await AssertProblem(response, HttpStatusCode.InternalServerError, "invalid_stored_application");
        var error = logs.Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error && e.Message.StartsWith("Admin Legacy status lookup"))
            .Subject;
        error.Properties["ApplicationId"].Should().Be(application.Id.ToString());
        error.Properties["WorkItemId"].Should().Be(workItem);
        error.Message.Should().NotContain("Harbourside");
    }

    [Fact]
    public async Task AStatusLookupTheDatabaseStopped_Is503_AsAProblem()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<ILegacyApplicationRead, TimedOutRead>()
            )
        );
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            ServiceClientSigner.Request(HttpMethod.Get, StatusesQuery(Guid.NewGuid()), AdminClientId, AdminSecret),
            Ct
        );

        await AssertProblem(response, HttpStatusCode.ServiceUnavailable, "query_timeout");
    }

    [Theory]
    [InlineData(StatusesPath + "?workItemId=3f2b8c1e-4d5a-4b6c-9e7f-0a1b2c3d4e5f")]
    [InlineData(MappingPath)]
    public async Task AnotherKnownClient_CannotReadStatusesOrTheMapping(string path)
    {
        var response = await Get(path, ReExClientId, ReExSecret);

        await AssertProblem(response, HttpStatusCode.Forbidden, "forbidden");
    }

    // ---- RA-751: how each work item state is pushed as an application status ----

    [Fact]
    public async Task TheStatusMapping_IsHowEachPushedStateLandsOnTheApplicationStatus()
    {
        var response = await Get(MappingPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("statusPush")
            .EnumerateArray()
            .Select(p => $"{p.GetProperty("stateId").GetString()}={p.GetProperty("applicationStatus").GetString()}")
            .Should()
            .Equal(
                "submitted=Submitted",
                "duly-made=DulyMade",
                "assessment-started=DulyMade",
                // RA-751 AC6: assessment-in-progress and updated both push Updated.
                "assessment-in-progress=Updated",
                "updated=Updated",
                "awaiting-decision=AwaitingDecision",
                "approved=Approved",
                "rejected=Rejected"
            );
        body.GetProperty("queryPush").GetProperty("applicationStatus").GetString().Should().Be("Queried");
    }

    [Fact]
    public async Task AnUnknownAdminPath_Is404_AsAProblem()
    {
        var response = await Get("/admin/no-such-thing");

        await AssertProblem(response, HttpStatusCode.NotFound, "not_found");
    }
}
