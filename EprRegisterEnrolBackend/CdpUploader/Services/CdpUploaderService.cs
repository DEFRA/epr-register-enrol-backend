using System.Net.Http.Json;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(
        JsonSerializerDefaults.Web
    );

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

        request.Redirect = ToRelativeUri(request.Redirect);

        var requestJson = JsonSerializer.Serialize(request, ResponseJsonOptions);
        logger.LogInformation(
            "Calling CDP uploader POST {InitiateUrl} with body: {RequestBody}",
            initiateUrl,
            requestJson
        );

        var client = httpClientFactory.CreateClient("DefaultClient");

        HttpResponseMessage response;
        try
        {
            // CDP uploader is a Node service expecting camelCase JSON (s3Bucket, s3Path,
            // etc.) — without ResponseJsonOptions here, PostAsJsonAsync's PascalCase default
            // means fields like S3Bucket never reach it under a name it recognises, so the
            // bucket is effectively never passed even though the C# property is set.
            response = await client.PostAsJsonAsync(
                initiateUrl,
                request,
                ResponseJsonOptions,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to reach CDP uploader at {InitiateUrl} with body: {RequestBody}",
                initiateUrl,
                requestJson
            );
            throw;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "CDP uploader returned {Status} from {InitiateUrl} for request body {RequestBody}: {ResponseBody}",
                (int)response.StatusCode,
                initiateUrl,
                requestJson,
                responseBody
            );
            throw new HttpRequestException(
                $"CDP uploader initiate failed: {(int)response.StatusCode}"
            );
        }

        logger.LogInformation(
            "CDP uploader returned {Status} from {InitiateUrl}: {ResponseBody}",
            (int)response.StatusCode,
            initiateUrl,
            responseBody
        );

        var result = JsonSerializer.Deserialize<CdpInitiateResponse>(
            responseBody,
            ResponseJsonOptions
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

        logger.LogInformation(
            "CDP upload initiated: uploadId={UploadId}, uploadUrl={UploadUrl}, statusUrl={StatusUrl}",
            result.UploadId,
            result.UploadUrl,
            result.StatusUrl
        );
        return result;
    }

    public async Task<CdpStatusResponse> GetStatusAsync(
        string statusUrl,
        CancellationToken cancellationToken = default
    )
    {
        // Plain client, not "DefaultClient": that one carries header propagation, which
        // requires an active HTTP request context. This is polled from a BackgroundService
        // with no ambient request, so header propagation would throw.
        var client = httpClientFactory.CreateClient();

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(statusUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reach CDP uploader status at {StatusUrl}", statusUrl);
            throw;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"CDP uploader status check failed: {(int)response.StatusCode}"
            );
        }

        var result = JsonSerializer.Deserialize<CdpStatusResponse>(
            responseBody,
            ResponseJsonOptions
        );
        if (result is null)
        {
            throw new InvalidOperationException("CDP uploader returned empty status response.");
        }

        return result;
    }

    // CDP Uploader requires "redirect" to be a relative URI; strip scheme/host/port if present.
    private static string ToRelativeUri(string redirect)
    {
        if (Uri.TryCreate(redirect, UriKind.Absolute, out var absolute))
        {
            return absolute.PathAndQuery;
        }
        return redirect;
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
