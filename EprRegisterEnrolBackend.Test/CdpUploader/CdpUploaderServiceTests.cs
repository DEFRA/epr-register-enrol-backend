using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.CdpUploader;

public class CdpUploaderServiceTests
{
    private static CdpUploaderService BuildSut(
        HttpClient httpClient,
        string uploaderUrl = "http://localhost:7337"
    )
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("DefaultClient").Returns(httpClient);

        var config = Options.Create(new CdpUploaderConfig { Url = uploaderUrl });
        return new CdpUploaderService(factory, config, NullLogger<CdpUploaderService>.Instance);
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

        var result = await sut.InitiateAsync(request);

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
            }
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
        var sut = new CdpUploaderService(factory, config, NullLogger<CdpUploaderService>.Instance);

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

    private class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly object _body;

        public StubHttpMessageHandler(HttpStatusCode status, object body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var response = new HttpResponseMessage(_status) { Content = JsonContent.Create(_body) };
            return Task.FromResult(response);
        }
    }
}
