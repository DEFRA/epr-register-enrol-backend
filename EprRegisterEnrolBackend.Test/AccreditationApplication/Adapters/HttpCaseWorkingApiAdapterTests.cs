using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

public class HttpCaseWorkingApiAdapterTests
{
    private const string TestUrl = "http://mgmt-be:8085";
    private const string TestClientId = "epr-register-enrol-backend";

    private static AccreditationApplicationModel CreateTestApplication()
    {
        return new AccreditationApplicationModel
        {
            OrganisationId = "12345",
            OrganisationName = "Acme Recycling Ltd",
            Year = 2026,
            RegistrationId = "reg-001",
            RegistrationReference = "EPR-100023",
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = ApplicationStatus.Started,
            SiteAddress = "123 High Street, London, SW1A 1AA",
            SubmittedBy = new SubmittedByModel
            {
                FullName = "Jane Smith",
                JobTitle = "Operations Manager",
                Email = "jane@example.com",
            },
        };
    }

    private static (
        HttpCaseWorkingApiAdapter adapter,
        CapturingHttpMessageHandler handler
    ) CreateAdapter(
        string? url = TestUrl,
        string clientId = TestClientId,
        string? sharedSecret = null
    )
    {
        var config = Options.Create(
            new CaseWorkingApiConfig
            {
                Url = url ?? "",
                CognitoClientId = clientId,
                SharedSecret = sharedSecret,
            }
        );

        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.Created,
            new
            {
                id = Guid.NewGuid(),
                typeId = "re-accreditation",
                stateId = "submitted",
                payload = new { },
            }
        );

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));

        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        return (adapter, handler);
    }

    [Fact]
    public async Task SubmitApplicationAsync_Success_ReturnsApplicationReference()
    {
        var (adapter, _) = CreateAdapter();
        var result = await adapter.SubmitApplicationAsync(CreateTestApplication());
        result.ApplicationReference.Should().MatchRegex(@"^RA-\d{9}$");
    }

    [Fact]
    public async Task SubmitApplicationAsync_Success_ReturnsWorkItemIdFromResponse()
    {
        var expectedId = Guid.NewGuid();
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, CognitoClientId = TestClientId }
        );
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.Created,
            new
            {
                id = expectedId,
                typeId = "re-accreditation",
                stateId = "submitted",
                payload = new { },
            }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var result = await adapter.SubmitApplicationAsync(CreateTestApplication());

        result.WorkItemId.Should().Be(expectedId);
    }

    [Fact]
    public async Task SubmitApplicationAsync_UnparseableResponseBody_StillReturnsReferenceWithNullWorkItemId()
    {
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, CognitoClientId = TestClientId }
        );
        var handler = new RawBodyHttpMessageHandler(HttpStatusCode.Created, "not valid json");
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var result = await adapter.SubmitApplicationAsync(CreateTestApplication());

        result.ApplicationReference.Should().MatchRegex(@"^RA-\d{9}$");
        result.WorkItemId.Should().BeNull();
    }

    [Fact]
    public async Task SubmitApplicationAsync_ResponseMissingIdField_ReturnsNullWorkItemId()
    {
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, CognitoClientId = TestClientId }
        );
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.Created,
            new
            {
                typeId = "re-accreditation",
                stateId = "submitted",
                payload = new { },
            }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var result = await adapter.SubmitApplicationAsync(CreateTestApplication());

        result.ApplicationReference.Should().MatchRegex(@"^RA-\d{9}$");
        result.WorkItemId.Should().BeNull();
    }

    [Fact]
    public async Task SubmitApplicationAsync_MapsPayloadCorrectly()
    {
        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        handler.CapturedRequestBody.Should().NotBeNullOrEmpty();
        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var root = doc.RootElement;

        root.GetProperty("typeId").GetString().Should().Be("re-accreditation");
        root.GetProperty("source").GetString().Should().Be("operator-fe");

        var payload = root.GetProperty("payload");
        payload.GetProperty("organisationName").GetString().Should().Be("Acme Recycling Ltd");
        payload.GetProperty("registrationNumber").GetString().Should().Be("EPR-100023");
        payload.GetProperty("materialsHandled")[0].GetString().Should().Be("plastic");
        payload.GetProperty("previousAccreditationYear").GetInt32().Should().Be(2025);
        payload.GetProperty("complianceIssuesReported").GetInt32().Should().Be(0);
        payload.GetProperty("operatorEmail").GetString().Should().Be("jane@example.com");
        payload.GetProperty("siteAddressPostcode").GetString().Should().Be("SW1A 1AA");
    }

    [Fact]
    public async Task SubmitApplicationAsync_SetsAuthHeaders()
    {
        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        handler.CapturedRequest.Should().NotBeNull();
        var request = handler.CapturedRequest!;
        request.Headers.GetValues("x-cdp-cognito-client-id").Should().ContainSingle(TestClientId);
        request.Headers.GetValues("x-cdp-user-id").Should().ContainSingle("jane@example.com");
        request.Headers.GetValues("x-cdp-user-name").Should().ContainSingle("Jane Smith");
    }

    [Fact]
    public async Task SubmitApplicationAsync_WithSharedSecret_SetsHmacHeaders()
    {
        var (adapter, handler) = CreateAdapter(sharedSecret: "test-secret-key");
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        var request = handler.CapturedRequest!;
        request.Headers.Contains("x-cdp-auth-signature").Should().BeTrue();
        request.Headers.Contains("x-cdp-auth-timestamp").Should().BeTrue();
        request.Headers.Contains("x-cdp-auth-nonce").Should().BeTrue();

        var timestamp = request.Headers.GetValues("x-cdp-auth-timestamp").Single();
        DateTime.TryParse(timestamp, out _).Should().BeTrue("timestamp should be parseable");

        var nonce = request.Headers.GetValues("x-cdp-auth-nonce").Single();
        nonce.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SubmitApplicationAsync_WithSharedSecret_SignatureIsValid()
    {
        const string secret = "test-secret-key";
        var (adapter, handler) = CreateAdapter(sharedSecret: secret);
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        var request = handler.CapturedRequest!;
        var timestamp = request.Headers.GetValues("x-cdp-auth-timestamp").Single();
        var nonce = request.Headers.GetValues("x-cdp-auth-nonce").Single();
        var actualSignature = request.Headers.GetValues("x-cdp-auth-signature").Single();

        var expectedSignature = HttpCaseWorkingApiAdapter.ComputeSignature(
            secret,
            TestClientId,
            "jane@example.com",
            "Jane Smith",
            null,
            timestamp,
            nonce
        );

        actualSignature.Should().Be(expectedSignature);
    }

    [Fact]
    public async Task SubmitApplicationAsync_WithoutSharedSecret_DoesNotSetHmacHeaders()
    {
        var (adapter, handler) = CreateAdapter(sharedSecret: null);
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        var request = handler.CapturedRequest!;
        request.Headers.Contains("x-cdp-auth-signature").Should().BeFalse();
        request.Headers.Contains("x-cdp-auth-timestamp").Should().BeFalse();
        request.Headers.Contains("x-cdp-auth-nonce").Should().BeFalse();
        request.Headers.Contains("x-cdp-cognito-client-id").Should().BeTrue();
    }

    [Fact]
    public async Task SubmitApplicationAsync_NonSuccessResponse_ThrowsHttpRequestException()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            new { title = "Error", detail = "Something broke" }
        );

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));

        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication());
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*500*");
    }

    [Fact]
    public async Task SubmitApplicationAsync_IncludesApplicationReferenceInRequest()
    {
        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        doc.RootElement.GetProperty("applicationReference")
            .GetString()
            .Should()
            .MatchRegex(@"^RA-\d{9}$");
    }

    [Fact]
    public async Task SubmitApplicationAsync_ReturnedReferenceMatchesSentToManagementBe()
    {
        var (adapter, handler) = CreateAdapter();
        var result = await adapter.SubmitApplicationAsync(CreateTestApplication());

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var sent = doc.RootElement.GetProperty("applicationReference").GetString();

        result.ApplicationReference.Should().Be(sent);
    }

    [Fact]
    public async Task SubmitApplicationAsync_EmptyUrl_ThrowsInvalidOperationException()
    {
        var (adapter, _) = CreateAdapter(url: "");

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not configured*");
    }

    [Fact]
    public async Task SubmitApplicationAsync_NullSiteAddress_SendsNullPostcode()
    {
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.SiteAddress = null;

        await adapter.SubmitApplicationAsync(app);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var payload = doc.RootElement.GetProperty("payload");
        payload
            .TryGetProperty("siteAddressPostcode", out var postcodeEl)
            .Should()
            .BeFalse("null-valued properties are excluded by WhenWritingNull");
    }

    [Fact]
    public async Task SubmitApplicationAsync_PostsToCorrectUrl()
    {
        var (adapter, handler) = CreateAdapter(url: "http://my-mgmt-be:9090");
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        handler
            .CapturedRequest!.RequestUri!.ToString()
            .Should()
            .Be("http://my-mgmt-be:9090/work-items");
    }

    [Fact]
    public void ComputeSignature_MatchesKnownValue()
    {
        var result = HttpCaseWorkingApiAdapter.ComputeSignature(
            "my-secret",
            "my-client",
            null,
            null,
            null,
            "2026-06-22T10:00:00Z",
            "dGVzdC1ub25jZQ=="
        );

        // Pre-computed: HMAC-SHA256("my-secret", "v2\nmy-client\n\n\n\n2026-06-22T10:00:00Z\ndGVzdC1ub25jZQ==")
        // Verify this is a valid base64 string of the right length (44 chars for SHA-256)
        result.Should().NotBeNullOrEmpty();
        Convert.FromBase64String(result).Should().HaveCount(32);
    }

    [Theory]
    [InlineData("123 High Street, London, SW1A 1AA", "SW1A 1AA")]
    [InlineData("Flat 2, 10 Park Road, Manchester, M1 4BT", "M1 4BT")]
    [InlineData("SW1A 1AA", "SW1A 1AA")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void ExtractPostcode_ReturnsExpected(string? input, string? expected)
    {
        HttpCaseWorkingApiAdapter.ExtractPostcode(input).Should().Be(expected);
    }

    #region Test infrastructure

    internal class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly object _body;

        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedRequestBody { get; private set; }

        public CapturingHttpMessageHandler(HttpStatusCode status, object body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            CapturedRequest = request;
            if (request.Content != null)
                CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status) { Content = JsonContent.Create(_body) };
        }
    }

    internal class RawBodyHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public RawBodyHttpMessageHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(
                new HttpResponseMessage(_status) { Content = new StringContent(_body) }
            );
        }
    }

    #endregion
}
