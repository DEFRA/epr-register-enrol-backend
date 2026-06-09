using System.Net.Http.Json;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.CdpUploader.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.CdpUploader.Services;

public class CdpUploaderService(
    IHttpClientFactory httpClientFactory,
    IOptions<CdpUploaderConfig> config,
    ILogger<CdpUploaderService> logger
) : ICdpUploaderService
{
    private readonly CdpUploaderConfig _config = config.Value;

    public async Task<CdpInitiateResponse> InitiateAsync(
        CdpInitiateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var uploaderUrl = _config.Url;
        if (string.IsNullOrWhiteSpace(uploaderUrl))
        {
            logger.LogError(
                "CDP_UPLOADER_URL is not configured. File uploads cannot be initiated."
            );
            throw new InvalidOperationException("CDP uploader URL is not configured.");
        }

        var initiateUrl = $"{uploaderUrl.TrimEnd('/')}/initiate";
        logger.LogInformation(
            "Initiating CDP upload to {InitiateUrl} for path {S3Path}",
            initiateUrl,
            request.S3Path
        );

        var client = httpClientFactory.CreateClient("DefaultClient");

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(initiateUrl, request, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reach CDP uploader at {InitiateUrl}", initiateUrl);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "CDP uploader returned {Status} from {InitiateUrl}: {Body}",
                (int)response.StatusCode,
                initiateUrl,
                body
            );
            throw new HttpRequestException(
                $"CDP uploader initiate failed: {(int)response.StatusCode}"
            );
        }

        var result = await response.Content.ReadFromJsonAsync<CdpInitiateResponse>(
            cancellationToken: cancellationToken
        );
        if (result is null)
        {
            logger.LogError("CDP uploader returned empty body from {InitiateUrl}", initiateUrl);
            throw new InvalidOperationException("CDP uploader returned empty initiate response.");
        }

        // Rewrite uploadUrl/statusUrl — CDP may return internal Docker hostnames.
        // Replace with the configured uploader URL so the browser can reach it.
        result = result with
        {
            UploadUrl = RewriteHost(result.UploadUrl, uploaderUrl),
            StatusUrl = RewriteHost(result.StatusUrl, uploaderUrl),
        };

        logger.LogInformation("CDP upload initiated: uploadId={UploadId}", result.UploadId);
        return result;
    }

    private static string RewriteHost(string url, string targetBase)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;
        try
        {
            var parsed = new Uri(url);
            var target = new Uri(targetBase);
            var builder = new UriBuilder(parsed)
            {
                Scheme = target.Scheme,
                Host = target.Host,
                Port = target.Port,
            };
            return builder.Uri.ToString();
        }
        catch
        {
            return url;
        }
    }
}
