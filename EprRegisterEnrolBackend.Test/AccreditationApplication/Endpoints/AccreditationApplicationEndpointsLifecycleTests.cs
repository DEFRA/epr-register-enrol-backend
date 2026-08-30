using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.ReEx;
using FluentAssertions;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// Branch-coverage top-up for Seed / Submit / Resubmit / Withdraw / QueryFromCaseManagement /
// StatusChangedFromCaseManagement. Deliberately a separate file from
// AccreditationApplicationEndpointsTests.cs (which other work is landing in parallel) so this
// only adds tests for branches that file does not already exercise.
public class AccreditationApplicationEndpointsLifecycleTests
    : IClassFixture<AccreditationApplicationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApplicationEndpointsLifecycleTests(
        AccreditationApplicationTestFactory factory
    )
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private void Reset()
    {
        _factory.FakePersistence.Clear();
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
            MaterialType = MaterialType.Steel,
            ApplicationStatus = status,
        };
        configure?.Invoke(app);
        _factory.FakePersistence.Seed(app);
        return app;
    }

    private static ReExResult<ReExAccreditationDto> MinimalAdapterSuccess(
        string orgId = "org-123",
        MaterialType material = MaterialType.Steel,
        int year = 2025,
        bool isExporter = false
    ) =>
        ReExResult<ReExAccreditationDto>.Success(
            new ReExAccreditationDto
            {
                AccreditationId = $"reex-acc-{orgId}-{material}-{year}",
                OrganisationId = orgId,
                MaterialType = material,
                Year = year,
                OrganisationName = "Stub Org Ltd",
                IsExporter = isExporter,
                OverseasSites = isExporter
                    ?
                    [
                        new OverseasSiteModel
                        {
                            SiteId = 1,
                            OrsId = "ORS-1",
                            SiteName = "Test Overseas Site",
                            Selected = true,
                        },
                    ]
                    : [],
            },
            200
        );

    // --- Seed ---

    [Fact]
    public async Task Seed_AdapterReturnsClientError_Returns503()
    {
        // IsUpstreamFailure is false for ClientError (unlike ServerError/Timeout/etc), so this
        // must fall through to the 503 branch rather than the 502 (upstream-failure) branch.
        Reset();
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
                        new ReExError(ReExErrorKind.ClientError, "bad request upstream"),
                        400
                    )
                )
            );

        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/reg-1/Steel/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Seed_WhenPriorYearIsExporter_PopulatesOverseasSites()
    {
        Reset();
        _factory
            .MockReExAdapter.GetAccreditationAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MaterialType>(),
                Arg.Any<int>()
            )
            .Returns(Task.FromResult(MinimalAdapterSuccess(isExporter: true)));

        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/reg-1/Steel/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.IsExporter.Should().BeTrue();
        body.OverseasSites.Should().NotBeNull();
        body.OverseasSites!.Sites.Should().ContainSingle(s => s.OrsId == "ORS-1");
    }

    // --- Submit ---

    [Fact]
    public async Task Submit_InvalidRequest_Returns400()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started);

        var request = new SubmitRequest { FullName = "", JobTitle = "" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await _factory
            .MockCaseWorkingAdapter.DidNotReceive()
            .SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Submit_ApplicationNotFound_Returns404()
    {
        Reset();
        var request = new SubmitRequest
        {
            FullName = "John",
            JobTitle = "Manager",
            Email = "j@x.com",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Submit_WhenAdapterTimesOut_Returns504GatewayTimeout()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Started,
            configure: a =>
            {
                a.Prns.SectionStatus = SectionStatus.Completed;
                a.BusinessPlan.SectionStatus = SectionStatus.Completed;
                a.SamplingPlan.SectionStatus = SectionStatus.Completed;
            }
        );
        _factory
            .MockCaseWorkingAdapter.SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromException<CaseWorkingSubmissionResult>(
                    new CaseWorkingApiTimeoutException(
                        "timed out",
                        new TaskCanceledException("timed out")
                    )
                )
            );

        var request = new SubmitRequest
        {
            FullName = "John",
            JobTitle = "Manager",
            Email = "j@x.com",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Started);
        stored.ApplicationReference.Should().BeNull();
    }

    // RA-503: OrgId must be resolved from ReEx and threaded onto the application passed to
    // SubmitApplicationAsync, so BuildPayload can send the operator/regulator-safe numeric
    // organisation number rather than leaving it unset.
    [Fact]
    public async Task Submit_ResolvesOrgIdFromReExAndPassesItToCaseWorkingAdapter()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Started,
            configure: a =>
            {
                a.Prns.SectionStatus = SectionStatus.Completed;
                a.BusinessPlan.SectionStatus = SectionStatus.Completed;
                a.SamplingPlan.SectionStatus = SectionStatus.Completed;
            }
        );
        _factory
            .MockReExAdapter.GetOrganisationNumberAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(ReExResult<int?>.Success(500500, 200)));
        _factory
            .MockCaseWorkingAdapter.SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new CaseWorkingSubmissionResult("AP26EA500500", null)));

        var request = new SubmitRequest
        {
            FullName = "John",
            JobTitle = "Manager",
            Email = "j@x.com",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockCaseWorkingAdapter.Received(1)
            .SubmitApplicationAsync(
                Arg.Is<AccreditationApplicationModel>(a => a.OrgId == 500500),
                Arg.Any<CancellationToken>()
            );
    }

    // RA-503: a ReEx lookup failure must not block submission - OrgId is left null and the
    // adapter is still called, same fallback-friendly behaviour as ResolveOrgIdAsync elsewhere.
    [Fact]
    public async Task Submit_ReExOrgIdLookupFails_StillSubmitsWithNullOrgId()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Started,
            configure: a =>
            {
                a.Prns.SectionStatus = SectionStatus.Completed;
                a.BusinessPlan.SectionStatus = SectionStatus.Completed;
                a.SamplingPlan.SectionStatus = SectionStatus.Completed;
            }
        );
        _factory
            .MockReExAdapter.GetOrganisationNumberAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromResult(
                    ReExResult<int?>.Fail(new ReExError(ReExErrorKind.ServerError, "down"), 500)
                )
            );
        _factory
            .MockCaseWorkingAdapter.SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new CaseWorkingSubmissionResult("AP26EA", null)));

        var request = new SubmitRequest
        {
            FullName = "John",
            JobTitle = "Manager",
            Email = "j@x.com",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockCaseWorkingAdapter.Received(1)
            .SubmitApplicationAsync(
                Arg.Is<AccreditationApplicationModel>(a => a.OrgId == null),
                Arg.Any<CancellationToken>()
            );
    }

    // RA-503: the operator's real, frontend-computed bank payment reference (SubmitRequest.
    // PaymentReference) must reach the case-working adapter on the application it submits, so
    // BuildPayload can forward it to management-be instead of leaving the regulator's
    // duly-making page to show a different reference than the one the operator was told to quote.
    [Fact]
    public async Task Submit_ForwardsPaymentReferenceFromRequestToTheSubmittedApplication()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Started,
            configure: a =>
            {
                a.Prns.SectionStatus = SectionStatus.Completed;
                a.BusinessPlan.SectionStatus = SectionStatus.Completed;
                a.SamplingPlan.SectionStatus = SectionStatus.Completed;
            }
        );
        _factory
            .MockCaseWorkingAdapter.SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new CaseWorkingSubmissionResult("AP26EA500500", null)));

        var request = new SubmitRequest
        {
            FullName = "John",
            JobTitle = "Manager",
            Email = "j@x.com",
            PaymentReference = "PR/PK/REP/500500",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockCaseWorkingAdapter.Received(1)
            .SubmitApplicationAsync(
                Arg.Is<AccreditationApplicationModel>(a =>
                    a.PaymentReference == "PR/PK/REP/500500"
                ),
                Arg.Any<CancellationToken>()
            );
    }

    // --- Resubmit ---

    [Fact]
    public async Task Resubmit_ApplicationNotFound_Returns404()
    {
        Reset();
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/resubmit",
            new ResubmitRequest
            {
                FullName = "Jane",
                Email = "jane@example.com",
                Role = "Manager",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resubmit_NullContactDetails_DefaultsToEmptyStringsAndSucceeds()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.CaseManagementWorkItemId = Guid.NewGuid();
                a.BusinessPlan.SectionStatus = SectionStatus.Queried;
                a.Query = new AccreditationApplicationQuery
                {
                    QueryNote = "clarify",
                    QueriedSectionKeys = ["business-plan"],
                };
            }
        );
        _factory
            .MockCaseWorkingAdapter.ResumeFromQueryAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new ResumeFromQueryResult(true)));

        // FullName/Email/Role all omitted -> null on the wire.
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/resubmit",
            new ResubmitRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Query!.QuerySubmissions.Should().ContainSingle();
        var contact = body.Query.QuerySubmissions[0].QuerySubmitterContactDetails;
        contact.FullName.Should().BeEmpty();
        contact.Email.Should().BeEmpty();
        contact.Role.Should().BeEmpty();
        await _factory
            .MockCaseWorkingAdapter.Received(1)
            .ResumeFromQueryAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Is<QuerySubmitterContactDetails>(c =>
                    c.FullName == "" && c.Email == "" && c.Role == ""
                ),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Resubmit_QueriedSectionKeysContainsUnknownKey_IgnoresUnknownKeyButProcessesKnownOnes()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.CaseManagementWorkItemId = Guid.NewGuid();
                a.BusinessPlan.SectionStatus = SectionStatus.Queried;
                a.Query = new AccreditationApplicationQuery
                {
                    QueryNote = "clarify",
                    // "not-a-real-key" cannot be mapped back to an OperatorSection and must be
                    // silently dropped rather than blowing up the resubmit.
                    QueriedSectionKeys = ["business-plan", "not-a-real-key"],
                };
            }
        );
        _factory
            .MockCaseWorkingAdapter.ResumeFromQueryAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new ResumeFromQueryResult(true)));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/resubmit",
            new ResubmitRequest
            {
                FullName = "Jane",
                Email = "jane@example.com",
                Role = "Manager",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Updated);
        body.BusinessPlan.SectionStatus.Should().Be(SectionStatus.NotStarted);
    }

    [Fact]
    public async Task Resubmit_SectionAlreadyResolved_LeavesItsStatusUntouched()
    {
        // BusinessPlan is listed as queried, but its live SectionStatus is already Completed (not
        // Queried) by the time resubmit runs — the recompute-on-resubmit branch must not fire, so
        // it should not be forced back through ComputeCurrentStatus.
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.CaseManagementWorkItemId = Guid.NewGuid();
                a.BusinessPlan.SectionStatus = SectionStatus.Completed;
                a.BusinessPlan.NewInfrastructurePercent = 10;
                a.BusinessPlan.PriceSupportPercent = 10;
                a.BusinessPlan.BusinessCollectionsPercent = 20;
                a.BusinessPlan.CommunicationsPercent = 20;
                a.BusinessPlan.NewMarketsPercent = 20;
                a.BusinessPlan.NewUsesPercent = 20;
                a.Query = new AccreditationApplicationQuery
                {
                    QueryNote = "clarify",
                    QueriedSectionKeys = ["business-plan"],
                };
            }
        );
        _factory
            .MockCaseWorkingAdapter.ResumeFromQueryAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new ResumeFromQueryResult(true)));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/resubmit",
            new ResubmitRequest
            {
                FullName = "Jane",
                Email = "jane@example.com",
                Role = "Manager",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.BusinessPlan.SectionStatus.Should().Be(SectionStatus.Completed);
    }

    [Fact]
    public async Task Resubmit_QueryIsNull_InitializesQueryAndSucceeds()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.CaseManagementWorkItemId = Guid.NewGuid();
                a.Query = null;
            }
        );
        _factory
            .MockCaseWorkingAdapter.ResumeFromQueryAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new ResumeFromQueryResult(true)));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/resubmit",
            new ResubmitRequest
            {
                FullName = "Jane",
                Email = "jane@example.com",
                Role = "Manager",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Updated);
        body.Query.Should().NotBeNull();
        body.Query!.QuerySubmissions.Should().ContainSingle();
    }

    [Fact]
    public async Task Resubmit_CaseManagementPushesStatusChangeDuringAdapterCall_SucceedsAndKeepsBothWritersChanges()
    {
        // RA-519 regression: ManagementBe's resume-from-query handling can synchronously call
        // back into this service's own case-management/{workItemId}/status webhook while this
        // endpoint is still awaiting ResumeFromQueryAsync. That webhook does its own
        // read-modify-write against the very same document. Before RA-519, Resubmit's own persist
        // was a whole-document replace (guarded only by the RA-516 Version field) — so once the
        // webhook moved the document's Version on, Resubmit's own write (still working off its
        // pre-webhook-read copy) was rejected outright and this endpoint 500'd, even though
        // ManagementBe's transition and the webhook's own Mongo write had both already succeeded.
        // This proves Resubmit's own write now survives that race instead of failing.
        Reset();
        var workItemId = Guid.NewGuid();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.CaseManagementWorkItemId = workItemId;
                a.BusinessPlan.SectionStatus = SectionStatus.Queried;
                a.Query = new AccreditationApplicationQuery
                {
                    QueryNote = "clarify",
                    QueriedSectionKeys = ["business-plan"],
                };
            }
        );

        _factory
            .MockCaseWorkingAdapter.ResumeFromQueryAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(async _ =>
            {
                // Simulates ManagementBe's synchronous push-back into this service's own webhook,
                // interleaved between Resubmit's read and its own persist.
                using var webhookRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"/api/v1/accreditation-applications/case-management/{workItemId}/status"
                )
                {
                    Content = JsonContent.Create(
                        new StatusChangedFromCaseManagementRequest
                        {
                            ToStateId = "duly-made",
                            ActionId = "some-action",
                            OccurredAt = DateTime.UtcNow,
                        },
                        options: JsonOptions
                    ),
                };
                var webhookResponse = await _client.SendAsync(
                    webhookRequest,
                    TestContext.Current.CancellationToken
                );
                webhookResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                return new ResumeFromQueryResult(true);
            });

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/resubmit",
            new ResubmitRequest
            {
                FullName = "Jane",
                Email = "jane@example.com",
                Role = "Manager",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.OK,
                "the endpoint's own persist must survive the interleaved webhook write instead of 500ing"
            );
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        // Resubmit's own fields landed...
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Updated);
        body.Query!.QueriedSectionKeys.Should().BeEmpty();
        body.Query.QuerySubmissions.Should().ContainSingle();
        body.BusinessPlan.SectionStatus.Should().Be(SectionStatus.NotStarted);

        // ...and re-fetching independently (rather than trusting the in-memory response object)
        // confirms it actually persisted.
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Updated);
        stored.Query!.QuerySubmissions.Should().ContainSingle();
        stored.Query.QueriedSectionKeys.Should().BeEmpty();
    }

    // --- Withdraw ---

    [Fact]
    public async Task Withdraw_ApplicationNotFound_Returns404()
    {
        Reset();
        var request = new WithdrawRequest { Reason = "No longer required" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Withdraw_QueriedSectionAlreadyResolved_LeavesItsStatusUntouched()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.BusinessPlan.SectionStatus = SectionStatus.Completed;
                a.BusinessPlan.NewInfrastructurePercent = 10;
                a.BusinessPlan.PriceSupportPercent = 10;
                a.BusinessPlan.BusinessCollectionsPercent = 20;
                a.BusinessPlan.CommunicationsPercent = 20;
                a.BusinessPlan.NewMarketsPercent = 20;
                a.BusinessPlan.NewUsesPercent = 20;
                a.Query = new AccreditationApplicationQuery
                {
                    QueryNote = "clarify",
                    QueriedSectionKeys = ["business-plan"],
                };
            }
        );
        _factory
            .MockCaseWorkingAdapter.WithdrawApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new WithdrawResult(true)));

        var request = new WithdrawRequest { Reason = "No longer required" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Withdrawn);
        body.BusinessPlan.SectionStatus.Should().Be(SectionStatus.Completed);
    }

    [Fact]
    public async Task Withdraw_QueriedSectionKeysContainsUnknownKey_IgnoresUnknownKeyAndSucceeds()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.BusinessPlan.SectionStatus = SectionStatus.Queried;
                a.Query = new AccreditationApplicationQuery
                {
                    QueryNote = "clarify",
                    QueriedSectionKeys = ["business-plan", "not-a-real-key"],
                };
            }
        );
        _factory
            .MockCaseWorkingAdapter.WithdrawApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new WithdrawResult(true)));

        var request = new WithdrawRequest { Reason = "No longer required" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Withdrawn);
        body.BusinessPlan.SectionStatus.Should().NotBe(SectionStatus.Queried);
    }

    [Fact]
    public async Task Withdraw_QueryIsNullWhileQueried_InitializesQueryAndSucceeds()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.BusinessPlan.SectionStatus = SectionStatus.Queried;
                a.Query = null;
            }
        );
        _factory
            .MockCaseWorkingAdapter.WithdrawApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new WithdrawResult(true)));

        var request = new WithdrawRequest { Reason = "No longer required" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Withdrawn);
        body.Query.Should().NotBeNull();
        body.Query!.QueriedSectionKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Withdraw_CaseManagementPushesStatusChangeDuringAdapterCall_SucceedsAndKeepsBothWritersChanges()
    {
        // RA-519: Withdraw has the identical read-mutate-whole-document-replace shape as Resubmit
        // and is equally vulnerable to the same race - it's only not observed in production today
        // because ManagementBe happens not to push withdrawn transitions back synchronously, which
        // is an implementation detail of another repo, not something this endpoint should rely on.
        // This test forces the race deliberately to prove Withdraw is now structurally safe too.
        Reset();
        var workItemId = Guid.NewGuid();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.CaseManagementWorkItemId = workItemId;
                a.BusinessPlan.SectionStatus = SectionStatus.Queried;
                a.Query = new AccreditationApplicationQuery
                {
                    QueryNote = "clarify",
                    QueriedSectionKeys = ["business-plan"],
                };
            }
        );

        _factory
            .MockCaseWorkingAdapter.WithdrawApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(async _ =>
            {
                // Simulates ManagementBe's synchronous push-back into this service's own webhook,
                // interleaved between Withdraw's read and its own persist.
                using var webhookRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"/api/v1/accreditation-applications/case-management/{workItemId}/status"
                )
                {
                    Content = JsonContent.Create(
                        new StatusChangedFromCaseManagementRequest
                        {
                            ToStateId = "duly-made",
                            ActionId = "some-action",
                            OccurredAt = DateTime.UtcNow,
                        },
                        options: JsonOptions
                    ),
                };
                var webhookResponse = await _client.SendAsync(
                    webhookRequest,
                    TestContext.Current.CancellationToken
                );
                webhookResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                return new WithdrawResult(true);
            });

        var request = new WithdrawRequest { Reason = "No longer required" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.OK,
                "the endpoint's own persist must survive the interleaved webhook write instead of 500ing"
            );
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Withdrawn);
        body.WithdrawalReason.Should().Be("No longer required");
        body.Query!.QueriedSectionKeys.Should().BeEmpty();
        body.BusinessPlan.SectionStatus.Should().Be(SectionStatus.NotStarted);

        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Withdrawn);
        stored.WithdrawalReason.Should().Be("No longer required");
        stored.Query!.QueriedSectionKeys.Should().BeEmpty();
    }

    // --- QueryFromCaseManagement: X-Correlation-Id header present branch ---

    [Fact]
    public async Task QueryFromCaseManagement_WithCorrelationIdHeader_Succeeds()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.Submitted,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/accreditation-applications/case-management/{workItemId}/query"
        )
        {
            Content = JsonContent.Create(
                new { queryNote = "note", sectionKeys = new[] { "business-plan" } }
            ),
        };
        httpRequest.Headers.Add("X-Correlation-Id", "corr-abc-123");

        var response = await _client.SendAsync(httpRequest, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Queried);
    }

    [Fact]
    public async Task QueryFromCaseManagement_ValidationFailureWithCorrelationIdHeader_Returns400()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(configure: a => a.CaseManagementWorkItemId = workItemId);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/accreditation-applications/case-management/{workItemId}/query"
        )
        {
            Content = JsonContent.Create(
                new { queryNote = "note", sectionKeys = new[] { "not-a-real-key" } }
            ),
        };
        httpRequest.Headers.Add("X-Correlation-Id", "corr-def-456");

        var response = await _client.SendAsync(httpRequest, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- StatusChangedFromCaseManagement: unmapped toStateId branch ---

    [Fact]
    public async Task StatusChangedFromCaseManagement_UnmappedState_IsNoOpForStatusButUpdatesWatermark()
    {
        // "unmapped-future-state" has no arm in MapCaseManagementStateToApplicationStatus, so
        // mappedStatus is null: the terminal-status guard and the approve/reject legality check
        // must both be skipped (short-circuited), ApplicationStatus must not change, but the
        // ordering watermark must still be updated.
        Reset();
        var workItemId = Guid.NewGuid();
        var app = SeedApplication(
            status: ApplicationStatus.Submitted,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );
        var occurredAt = DateTime.UtcNow;

        var request = new StatusChangedFromCaseManagementRequest
        {
            ToStateId = "unmapped-future-state",
            ActionId = "some-future-action",
            OccurredAt = occurredAt,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Submitted);
        stored.CaseManagementStatusUpdatedAt.Should().Be(occurredAt);
    }

    [Fact]
    public async Task StatusChangedFromCaseManagement_UnmappedStateFromTerminalStatus_StillReturnsOkAndLeavesStatusUnchanged()
    {
        // Even from a terminal status, an unmapped push must not be rejected: the terminal guard
        // only fires when mappedStatus is not null, so an unmapped push is exempt and only the
        // watermark should move.
        Reset();
        var workItemId = Guid.NewGuid();
        var app = SeedApplication(
            status: ApplicationStatus.Withdrawn,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );
        var occurredAt = DateTime.UtcNow;

        var request = new StatusChangedFromCaseManagementRequest
        {
            ToStateId = "unmapped-future-state",
            ActionId = "some-future-action",
            OccurredAt = occurredAt,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Withdrawn);
        stored.CaseManagementStatusUpdatedAt.Should().Be(occurredAt);
    }

    [Fact]
    public async Task StatusChangedFromCaseManagement_WithCorrelationIdHeader_Succeeds()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.Submitted,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status"
        )
        {
            Content = JsonContent.Create(
                new StatusChangedFromCaseManagementRequest
                {
                    ToStateId = "duly-made",
                    ActionId = "duly-made-transition",
                    OccurredAt = DateTime.UtcNow,
                },
                options: JsonOptions
            ),
        };
        httpRequest.Headers.Add("X-Correlation-Id", "corr-ghi-789");

        var response = await _client.SendAsync(httpRequest, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.DulyMade);
    }
}
