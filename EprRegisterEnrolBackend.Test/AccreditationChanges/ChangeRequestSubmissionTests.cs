using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;

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
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var payload = body.RootElement.GetProperty("payload").Clone();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new
                {
                    id = Interlocked.Increment(ref _attempts) == 1 ? Guid.Empty : Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    typeId = "add-overseas-site", applicationReference = "AP26EXAMPLE", payload,
                }),
            };
        }
    }
}
