using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.CdpUploader;

public class CdpUploaderServiceTests
{
    // Mirrors the camelCase convention the real (Node.js) cdp-uploader service uses on the
    // wire, so tests that inspect the captured request/response bodies exercise the same
    // casing as production rather than .NET's PascalCase-preserving default.
    private static readonly JsonSerializerOptions CamelCaseOptions = new(
        JsonSerializerDefaults.Web
    );

    private static CdpUploaderService BuildSut(
        HttpClient httpClient,
        string uploaderUrl = "http://localhost:7337"
    )
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("DefaultClient").Returns(httpClient);

        var config = Options.Create(new CdpUploaderConfig { Url = uploaderUrl });
        return new CdpUploaderService(factory, config, EnabledNullLogger<CdpUploaderService>.Instance);
    }

    [Fact]
    public async Task InitiateAsync_SuccessResponse_ReturnsResponse()
    {
        var cdpResponse = new CdpInitiateResponse
        {
            UploadId = "upload-123",
            UploadUrl = "http://localhost:7337/upload/upload-123",
            StatusUrl = "http://localhost:7337/status/upload-123",
        };

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, cdpResponse);
        using var client = new HttpClient(handler);
        var sut = BuildSut(client);

        var request = new CdpInitiateRequest
        {
            Redirect = "http://frontend/redirect",
            S3Bucket = "my-bucket",
            S3Path = "uploads/test.csv",
        };

        var result = await sut.InitiateAsync(request, TestContext.Current.CancellationToken);

        result.UploadId.Should().Be("upload-123");
        result.UploadUrl.Should().Be("http://localhost:7337/upload/upload-123");
        result.StatusUrl.Should().Be("http://localhost:7337/status/upload-123");
    }

    [Fact]
    public async Task InitiateAsync_RewritesHostInUrls()
    {
        var cdpResponse = new CdpInitiateResponse
        {
            UploadId = "upload-456",
            UploadUrl = "http://cdp-internal-docker:9000/upload/upload-456",
            StatusUrl = "http://cdp-internal-docker:9000/status/upload-456",
        };

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, cdpResponse);
        using var client = new HttpClient(handler);
        var sut = BuildSut(client, "http://localhost:7337");

        var result = await sut.InitiateAsync(
            new CdpInitiateRequest
            {
                Redirect = "http://frontend/redirect",
                S3Bucket = "bucket",
                S3Path = "path",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.UploadUrl.Should().StartWith("http://localhost:7337");
        result.StatusUrl.Should().StartWith("http://localhost:7337");
    }

    [Fact]
    public async Task InitiateAsync_FailResponse_ThrowsHttpRequestException()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "error");
        using var client = new HttpClient(handler);
        var sut = BuildSut(client);

        var act = async () =>
            await sut.InitiateAsync(
                new CdpInitiateRequest
                {
                    Redirect = "http://x",
                    S3Bucket = "b",
                    S3Path = "p",
                }
            );

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task InitiateAsync_EmptyUrl_ThrowsInvalidOperationException()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var config = Options.Create(new CdpUploaderConfig { Url = "" });
        var sut = new CdpUploaderService(factory, config, EnabledNullLogger<CdpUploaderService>.Instance);

        var act = async () =>
            await sut.InitiateAsync(
                new CdpInitiateRequest
                {
                    Redirect = "http://x",
                    S3Bucket = "b",
                    S3Path = "p",
                }
            );

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not configured*");
    }

    [Fact]
    public async Task InitiateAsync_ConvertsAbsoluteRedirectToRelativePath()
    {
        var cdpResponse = new CdpInitiateResponse
        {
            UploadId = "upload-123",
            UploadUrl = "http://localhost:7337/upload/upload-123",
            StatusUrl = "http://localhost:7337/status/upload-123",
        };

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, cdpResponse);
        using var client = new HttpClient(handler);
        var sut = BuildSut(client);

        await sut.InitiateAsync(
            new CdpInitiateRequest
            {
                Redirect = "http://frontend.example.com/accreditation/x/status?y=1",
                S3Bucket = "my-bucket",
                S3Path = "uploads/test.csv",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        var sentRequest = await handler.LastRequest!.Content!.ReadFromJsonAsync<CdpInitiateRequest>(
            CamelCaseOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        sentRequest!.Redirect.Should().Be("/accreditation/x/status?y=1");
    }

    [Fact]
    public async Task InitiateAsync_LeavesAlreadyRelativeRedirectUnchanged()
    {
        var cdpResponse = new CdpInitiateResponse
        {
            UploadId = "upload-123",
            UploadUrl = "http://localhost:7337/upload/upload-123",
            StatusUrl = "http://localhost:7337/status/upload-123",
        };

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, cdpResponse);
        using var client = new HttpClient(handler);
        var sut = BuildSut(client);

        await sut.InitiateAsync(
            new CdpInitiateRequest
            {
                Redirect = "/already/relative",
                S3Bucket = "my-bucket",
                S3Path = "uploads/test.csv",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        var sentRequest = await handler.LastRequest!.Content!.ReadFromJsonAsync<CdpInitiateRequest>(
            CamelCaseOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        sentRequest!.Redirect.Should().Be("/already/relative");
    }

    [Fact]
    public async Task InitiateAsync_MalformedRedirect_PassesThroughUnchanged()
    {
        var cdpResponse = new CdpInitiateResponse
        {
            UploadId = "upload-123",
            UploadUrl = "http://localhost:7337/upload/upload-123",
            StatusUrl = "http://localhost:7337/status/upload-123",
        };

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, cdpResponse);
        using var client = new HttpClient(handler);
        var sut = BuildSut(client);

        await sut.InitiateAsync(
            new CdpInitiateRequest
            {
                Redirect = "",
                S3Bucket = "my-bucket",
                S3Path = "uploads/test.csv",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        var sentRequest = await handler.LastRequest!.Content!.ReadFromJsonAsync<CdpInitiateRequest>(
            CamelCaseOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        sentRequest!.Redirect.Should().Be("");
    }

    [Fact]
    public async Task InitiateAsync_SendsRequestBodyWithCamelCaseKeys()
    {
        // Regression guard: PostAsJsonAsync without explicit options defaults to PascalCase,
        // but cdp-uploader (a Node.js service) expects camelCase (s3Bucket, s3Path, etc.) — a
        // mismatch here means the bucket silently never reaches cdp-uploader under a name it
        // recognises, breaking the upload flow without any visible error.
        var cdpResponse = new CdpInitiateResponse
        {
            UploadId = "upload-789",
            UploadUrl = "http://localhost:7337/upload/upload-789",
            StatusUrl = "http://localhost:7337/status/upload-789",
        };
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, cdpResponse);
        using var client = new HttpClient(handler);
        var sut = BuildSut(client);

        await sut.InitiateAsync(
            new CdpInitiateRequest
            {
                Redirect = "/frontend/redirect",
                Callback = "http://backend/callback",
                S3Bucket = "my-bucket",
                S3Path = "uploads/test.csv",
                MimeTypes = ["application/pdf"],
                MaxFileSize = 1024,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        var bytes = await handler.LastRequest!.Content!.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        root.GetProperty("redirect").GetString().Should().Be("/frontend/redirect");
        root.GetProperty("callback").GetString().Should().Be("http://backend/callback");
        root.GetProperty("s3Bucket").GetString().Should().Be("my-bucket");
        root.GetProperty("s3Path").GetString().Should().Be("uploads/test.csv");
        root.GetProperty("mimeTypes")[0].GetString().Should().Be("application/pdf");
        root.GetProperty("maxFileSize").GetInt64().Should().Be(1024);
    }

    private class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly object _body;

        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHttpMessageHandler(HttpStatusCode status, object body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                LastRequest = request;
                LastRequest.Content = new ByteArrayContent(bytes);
                foreach (var header in request.Content.Headers)
                {
                    LastRequest.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            else
            {
                LastRequest = request;
            }

            var response = new HttpResponseMessage(_status)
            {
                Content = JsonContent.Create(_body, options: CamelCaseOptions),
            };
            return response;
        }
    }
}
