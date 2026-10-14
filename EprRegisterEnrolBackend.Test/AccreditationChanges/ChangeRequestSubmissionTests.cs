using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using EprRegisterEnrolBackend.Auth;
using System.Security.Cryptography;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

public class ChangeRequestSubmissionTests(MongoIntegrationFixture fixture)
{
    private const string Target = "/api/v1/accreditation-changes/50006/aaa000000000000000050006/reex-acc-50006-Glass-2026/Glass/2026";
    private const string Secret = "spike-submit-test";

    private EphemeralMongoTestFactory Factory() => new(fixture, "spike_submit", "Development",
        new Dictionary<string, string?>
        {
            ["AccreditationChangeSpike:Enabled"] = "true",
            ["AUTH_SHARED_SECRET:FRONTEND"] = Secret,
            ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = Secret,
            ["CaseWorking:UseStub"] = "false",
            ["CaseWorking:Url"] = "http://127.0.0.1:1",
            ["CASE_MANAGEMENT_API_SHARED_SECRET"] = "spike-management-test",
        });

    [Fact]
    public async Task ConcurrentSubmissions_ReserveOneRequestAndRetainItsIdentitiesAfterUnconfirmedDelivery()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        var initial = JsonDocument.Parse(await client.GetStringAsync(Target, TestContext.Current.CancellationToken));
        initial.RootElement.GetProperty("activeRequest").ValueKind.Should().Be(JsonValueKind.Null);

        var submissions = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            client.PostAsync(Target + "/submit", null, TestContext.Current.CancellationToken)));
        submissions.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.ServiceUnavailable);
        using var read = JsonDocument.Parse(await client.GetStringAsync(Target, TestContext.Current.CancellationToken));
        var active = read.RootElement.GetProperty("activeRequest");
        active.GetProperty("status").GetString().Should().Be("submission-pending");
        active.GetProperty("active").GetBoolean().Should().BeTrue();
        active.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        active.GetProperty("baselineSites").GetArrayLength().Should().Be(4);
        active.GetProperty("proposal").GetProperty("siteName").GetString().Should().Be("Spike Glass Recycling");
        var requestId = active.GetProperty("requestId").GetString();
        var siteId = active.GetProperty("proposedSiteId").GetString();
        await client.PostAsync(Target + "/submit", null, TestContext.Current.CancellationToken);
        using var refreshed = JsonDocument.Parse(await client.GetStringAsync(Target, TestContext.Current.CancellationToken));
        var resumed = refreshed.RootElement.GetProperty("activeRequest");
        resumed.GetProperty("requestId").GetString().Should().Be(requestId);
        resumed.GetProperty("proposedSiteId").GetString().Should().Be(siteId);
        refreshed.RootElement.GetProperty("status").GetString().Should().Be("Granted");
        refreshed.RootElement.GetProperty("overseasSites").GetArrayLength().Should().Be(4);
        (await client.GetStringAsync("/api/v1/accreditation-applications/50006", TestContext.Current.CancellationToken)).Should().Be("[]");
    }

    [Fact]
    public async Task ConfirmedRefusal_ReleasesTheSlotRetainsHistoryAndCannotCloseALaterRequest()
    {
        using var parent = Factory();
        var handler = new ReceiptHandler { ValidImmediately = true };
        using var factory = parent.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient("DefaultClient").ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        using var first = await client.PostAsync(Target + "/submit", null, TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var original = await first.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var body = new
        {
            requestId = original.GetProperty("requestId").GetString(),
            caseId = original.GetProperty("caseId").GetString(),
            proposedSiteId = original.GetProperty("proposedSiteId").GetString(),
            target = original.GetProperty("target"), outcome = "rejected",
        };
        using var unsigned = await client.PostAsJsonAsync("/api/v1/accreditation-changes/outcome", body, TestContext.Current.CancellationToken);
        unsigned.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var wrong = JsonSerializer.SerializeToNode(body)!;
        foreach (var field in new[] { "requestId", "caseId", "proposedSiteId" })
        {
            var mismatch = wrong.DeepClone();
            mismatch[field] = Guid.NewGuid().ToString();
            var rejected = await Callback(client, mismatch);
            rejected.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Conflict);
        }
        var wrongTarget = wrong.DeepClone();
        wrongTarget["target"]!["accreditationId"] = "unrelated";
        (await Callback(client, wrongTarget)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var unsupported = wrong.DeepClone();
        unsupported["outcome"] = "withdrawn";
        (await Callback(client, unsupported)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var before = JsonDocument.Parse(await client.GetStringAsync(Target, TestContext.Current.CancellationToken));
        before.RootElement.GetProperty("activeRequest").GetProperty("requestId").GetString().Should().Be(body.requestId);
        var closures = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Callback(client, body)));
        closures.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
        using var closed = JsonDocument.Parse(await client.GetStringAsync(Target, TestContext.Current.CancellationToken));
        closed.RootElement.GetProperty("activeRequest").ValueKind.Should().Be(JsonValueKind.Null);
        closed.RootElement.GetProperty("status").GetString().Should().Be("Granted");
        closed.RootElement.GetProperty("overseasSites").GetRawText().Should().Be(before.RootElement.GetProperty("overseasSites").GetRawText());
        var history = closed.RootElement.GetProperty("history");
        history.GetArrayLength().Should().Be(1);
        history[0].GetProperty("status").GetString().Should().Be("rejected");
        history[0].GetProperty("active").GetBoolean().Should().BeFalse();
        using var second = await client.PostAsync(Target + "/submit", null, TestContext.Current.CancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var next = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        foreach (var identity in new[] { "requestId", "proposedSiteId", "caseId" })
            next.GetProperty(identity).GetString().Should().NotBe(original.GetProperty(identity).GetString());
        next.GetProperty("proposal").GetRawText().Should().Be(original.GetProperty("proposal").GetRawText());
        (await Callback(client, body)).StatusCode.Should().Be(HttpStatusCode.OK);
        var conflict = wrong.DeepClone();
        conflict["caseId"] = next.GetProperty("caseId").GetString();
        (await Callback(client, conflict)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var refreshed = JsonDocument.Parse(await client.GetStringAsync(Target, TestContext.Current.CancellationToken));
        refreshed.RootElement.GetProperty("activeRequest").GetProperty("requestId").GetString().Should().Be(next.GetProperty("requestId").GetString());
        refreshed.RootElement.GetProperty("history").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task SeparateApprovals_AddDistinctSitesWithIdenticalDetailsAndReplayCannotChangeEarlierRequests()
    {
        using var parent = Factory();
        var handler = new ReceiptHandler { ValidImmediately = true };
        using var factory = parent.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient("DefaultClient").ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        var baseline = await ReadSummary(client);
        var applications = await client.GetStringAsync("/api/v1/accreditation-applications/50006", TestContext.Current.CancellationToken);
        var approved = new List<JsonElement>();
        var outcomes = new List<object>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var submission = await client.PostAsync(Target + "/submit", null, TestContext.Current.CancellationToken);
            submission.StatusCode.Should().Be(HttpStatusCode.OK);
            var request = await submission.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            request.GetProperty("baselineSites").GetRawText().Should().Be(baseline.GetProperty("overseasSites").GetRawText());
            foreach (var earlier in approved)
            {
                foreach (var field in new[] { "requestId", "caseId", "proposedSiteId" })
                    request.GetProperty(field).GetString().Should().NotBe(earlier.GetProperty(field).GetString());
                request.GetProperty("proposal").GetRawText().Should().Be(earlier.GetProperty("proposal").GetRawText());
            }
            var pending = await ReadSummary(client);
            pending.GetProperty("approvedAdditions").GetArrayLength().Should().Be(attempt);
            var outcome = new
            {
                requestId = request.GetProperty("requestId").GetString(),
                caseId = request.GetProperty("caseId").GetString(),
                proposedSiteId = request.GetProperty("proposedSiteId").GetString(),
                target = request.GetProperty("target"), outcome = "approved",
            };
            var wrong = JsonSerializer.SerializeToNode(outcome)!;
            foreach (var field in new[] { "requestId", "caseId", "proposedSiteId" })
            {
                var mismatch = wrong.DeepClone();
                mismatch[field] = Guid.NewGuid().ToString();
                (await Callback(client, mismatch)).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Conflict);
            }
            var wrongTarget = wrong.DeepClone();
            wrongTarget["target"]!["year"] = 2027;
            (await Callback(client, wrongTarget)).StatusCode.Should().Be(HttpStatusCode.Conflict);
            foreach (var old in outcomes)
                (await Callback(client, old)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ReadSummary(client)).GetProperty("activeRequest").GetProperty("requestId").GetString().Should().Be(outcome.requestId);
            var deliveries = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Callback(client, outcome)));
            deliveries.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
            var closed = await ReadSummary(client);
            closed.GetProperty("activeRequest").ValueKind.Should().Be(JsonValueKind.Null);
            closed.GetProperty("status").GetString().Should().Be("Granted");
            closed.GetProperty("overseasSites").GetRawText().Should().Be(baseline.GetProperty("overseasSites").GetRawText());
            closed.GetProperty("approvedAdditions").GetArrayLength().Should().Be(attempt + 1);
            var history = closed.GetProperty("history").EnumerateArray().Single(entry => entry.GetProperty("requestId").GetString() == outcome.requestId);
            history.GetProperty("status").GetString().Should().Be("approved");
            history.GetProperty("active").GetBoolean().Should().BeFalse();
            history.GetProperty("closedAt").ValueKind.Should().Be(JsonValueKind.String);
            foreach (var earlier in approved)
                closed.GetProperty("history").EnumerateArray().Single(entry => entry.GetProperty("requestId").GetString() == earlier.GetProperty("requestId").GetString())
                    .GetRawText().Should().Be(earlier.GetRawText());
            approved.Add(history.Clone());
            outcomes.Add(outcome);
            var conflict = wrong.DeepClone();
            conflict["outcome"] = "rejected";
            (await Callback(client, conflict)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        var beforeReplay = await ReadSummary(client);
        foreach (var outcome in outcomes)
            (await Callback(client, outcome)).StatusCode.Should().Be(HttpStatusCode.OK);
        var final = await ReadSummary(client);
        final.GetRawText().Should().Be(beforeReplay.GetRawText());
        var additions = final.GetProperty("approvedAdditions").EnumerateArray().ToArray();
        additions.Select(site => site.GetProperty("siteId").GetString()).Should().BeEquivalentTo(approved.Select(request => request.GetProperty("proposedSiteId").GetString()));
        foreach (var site in additions)
        {
            site.GetProperty("siteName").GetString().Should().Be("Spike Glass Recycling");
            site.GetProperty("siteAddress").GetString().Should().Be("25 Example Road, Hamburg");
            site.GetProperty("country").GetString().Should().Be("Germany");
        }
        (await client.GetStringAsync("/api/v1/accreditation-applications/50006", TestContext.Current.CancellationToken)).Should().Be(applications);
    }

    private static async Task<JsonElement> ReadSummary(HttpClient client)
    {
        using var response = await client.GetAsync(Target, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> Callback(HttpClient client, object body)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/accreditation-changes/outcome") { Content = JsonContent.Create(body) };
        message.Headers.Add("x-cdp-client-id", "epr-register-enrol-management-be");
        message.Headers.Add("x-cdp-auth-timestamp", timestamp);
        message.Headers.Add("x-cdp-auth-nonce", nonce);
        message.Headers.Add("x-cdp-auth-signature", CaseManagementAuthenticationHandler.ComputeSignature(
            Secret, "epr-register-enrol-management-be", null, null, timestamp, nonce));
        return client.SendAsync(message, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Summary_ReadsTheCorrelatedCaseStateWithoutClosingAnUnconfirmedOutcome()
    {
        using var parent = Factory();
        var handler = new ReceiptHandler { ValidImmediately = true };
        using var factory = parent.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient("DefaultClient").ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        (await client.PostAsync(Target + "/submit", null, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var state in new[] { "assessment-in-progress", "rejected", "approved" })
        {
            handler.State = state;
            using var read = JsonDocument.Parse(await client.GetStringAsync(Target, TestContext.Current.CancellationToken));
            read.RootElement.GetProperty("activeRequest").GetProperty("caseState").GetString().Should().Be(state);
            read.RootElement.GetProperty("activeRequest").GetProperty("active").GetBoolean().Should().BeTrue();
            read.RootElement.GetProperty("history").GetArrayLength().Should().Be(0);
        }
        handler.WrongCorrelation = true;
        using var mismatch = JsonDocument.Parse(await client.GetStringAsync(Target, TestContext.Current.CancellationToken));
        mismatch.RootElement.GetProperty("activeRequest").GetProperty("caseState").ValueKind.Should().Be(JsonValueKind.Null);
        handler.WrongCorrelation = false;
        handler.Unavailable = true;
        using var unavailable = JsonDocument.Parse(await client.GetStringAsync(Target, TestContext.Current.CancellationToken));
        unavailable.RootElement.GetProperty("activeRequest").GetProperty("caseState").ValueKind.Should().Be(JsonValueKind.Null);
        unavailable.RootElement.GetProperty("activeRequest").GetProperty("active").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("50007/aaa000000000000000050006/reex-acc-50006-Glass-2026/Glass/2026")]
    [InlineData("50006/other/reex-acc-50006-Glass-2026/Glass/2026")]
    [InlineData("50006/aaa000000000000000050006/other/Glass/2026")]
    [InlineData("50006/aaa000000000000000050006/reex-acc-50006-Glass-2026/Plastic/2026")]
    [InlineData("50006/aaa000000000000000050006/reex-acc-50006-Glass-2026/Glass/2027")]
    public async Task UnsupportedTarget_CannotReserveARequest(string target)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        var response = await client.PostAsync("/api/v1/accreditation-changes/" + target + "/submit", null, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var read = JsonDocument.Parse(await client.GetStringAsync(Target, TestContext.Current.CancellationToken));
        read.RootElement.GetProperty("activeRequest").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task RetriedReceipt_ConfirmsTheReservedRequestWithoutChangingItsProposalIdentity()
    {
        using var parent = Factory();
        var handler = new ReceiptHandler();
        using var factory = parent.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient("DefaultClient").ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        var first = await client.PostAsync(Target + "/submit", null, TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var pending = JsonDocument.Parse(await client.GetStringAsync(Target, TestContext.Current.CancellationToken));
        var original = pending.RootElement.GetProperty("activeRequest");
        var retry = await client.PostAsync(Target + "/submit", null, TestContext.Current.CancellationToken);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        using var confirmed = JsonDocument.Parse(await retry.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        confirmed.RootElement.GetProperty("requestId").GetString().Should().Be(original.GetProperty("requestId").GetString());
        confirmed.RootElement.GetProperty("proposedSiteId").GetString().Should().Be(original.GetProperty("proposedSiteId").GetString());
        confirmed.RootElement.GetProperty("status").GetString().Should().Be("submitted");
        confirmed.RootElement.GetProperty("caseId").GetString().Should().Be("11111111-1111-1111-1111-111111111111");
        var repeated = await client.PostAsync(Target + "/submit", null, TestContext.Current.CancellationToken);
        (await repeated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(await retry.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Submission_RequiresFrontendAuthentication()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var response = await client.PostAsync(Target + "/submit", null, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed class ReceiptHandler : HttpMessageHandler
    {
        private int _attempts;
        public bool ValidImmediately { get; set; }
        public string State { get; set; } = "submitted";
        public bool WrongCorrelation { get; set; }
        public bool Unavailable { get; set; }
        private readonly Dictionary<string, Guid> _cases = [];
        private JsonElement _payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(Unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = WrongCorrelation ? Guid.NewGuid().ToString() : request.RequestUri!.Segments.Last(),
                        typeId = "add-overseas-site", payload = _payload, stateId = State,
                    }),
                };
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var payload = body.RootElement.GetProperty("payload").Clone();
            _payload = payload;
            var requestId = payload.GetProperty("operatorRequestId").GetString()!;
            if (!_cases.TryGetValue(requestId, out var caseId))
                _cases[requestId] = caseId = _cases.Count == 0 ? Guid.Parse("11111111-1111-1111-1111-111111111111") : Guid.NewGuid();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new
                {
                    id = Interlocked.Increment(ref _attempts) == 1 && !ValidImmediately ? Guid.Empty : caseId,
                    typeId = "add-overseas-site", applicationReference = "AP26EXAMPLE", payload,
                }),
            };
        }
    }
}
