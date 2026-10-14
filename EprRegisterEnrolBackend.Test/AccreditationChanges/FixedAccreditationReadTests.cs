using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

public class FixedAccreditationReadTests(MongoIntegrationFixture fixture)
{
    private const string TargetPath = "/api/v1/accreditation-changes/50006/aaa000000000000000050006/reex-acc-50006-Glass-2026/Glass/2026";
    private const string Secret = "spike-read-test-secret";

    private EphemeralMongoTestFactory CreateFactory(bool enabled = true, string environment = "Development") =>
        new(fixture, "spike_read", environment, new Dictionary<string, string?>
        {
            ["AccreditationChangeSpike:Enabled"] = enabled.ToString(),
            ["AUTH_SHARED_SECRET:FRONTEND"] = Secret,
            ["CaseWorking:UseStub"] = "true",
        });

    [Fact]
    public async Task FixedTarget_ReadsBaselineAndProposalWithoutCreatingAnApplication()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);

        for (var refresh = 0; refresh < 2; refresh++)
        {
            using var response = await client.GetAsync(TargetPath, TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var target = body.RootElement;
            target.GetProperty("organisationName").GetString().Should().Be("Global Glass Exports");
            target.GetProperty("registrationReference").GetString().Should().Be("EXP-50006");
            target.GetProperty("accreditationId").GetString().Should().Be("reex-acc-50006-Glass-2026");
            target.GetProperty("year").GetInt32().Should().Be(2026);
            target.GetProperty("materialType").GetString().Should().Be("Glass");
            target.GetProperty("status").GetString().Should().Be("Granted");
            target.GetProperty("overseasSites").GetArrayLength().Should().Be(4);
            target.GetProperty("overseasSites")[0].GetProperty("siteName").GetString().Should().Be("Overseas Site 1 (Germany)");
            target.GetProperty("proposal").GetProperty("siteName").GetString().Should().Be("Spike Glass Recycling");
        }

        var applications = await client.GetStringAsync("/api/v1/accreditation-applications/50006", TestContext.Current.CancellationToken);
        applications.Should().Be("[]");
    }

    [Theory]
    [InlineData("50007/aaa000000000000000050006/reex-acc-50006-Glass-2026/Glass/2026")]
    [InlineData("50006/aaa000000000000000050007/reex-acc-50006-Glass-2026/Glass/2026")]
    [InlineData("50006/aaa000000000000000050006/other-accreditation/Glass/2026")]
    [InlineData("50006/aaa000000000000000050006/reex-acc-50006-Glass-2026/Plastic/2026")]
    [InlineData("50006/aaa000000000000000050006/reex-acc-50006-Glass-2026/Glass/2027")]
    public async Task UnsupportedTarget_IsNotEligible(string target)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        var response = await client.GetAsync($"/api/v1/accreditation-changes/{target}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-secret")]
    public async Task Read_RequiresFrontendAuthentication(string? token)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetAsync(TargetPath, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(false, "Development")]
    [InlineData(true, "Production")]
    public async Task Read_IsUnavailableOutsideEnabledDevelopment(bool enabled, string environment)
    {
        using var factory = CreateFactory(enabled, environment);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        var response = await client.GetAsync("/api/v1/accreditation-changes/target", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}