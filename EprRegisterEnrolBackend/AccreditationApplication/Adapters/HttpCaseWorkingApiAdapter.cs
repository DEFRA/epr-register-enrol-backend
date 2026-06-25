using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public class HttpCaseWorkingApiAdapter(
    IHttpClientFactory httpClientFactory,
    IOptions<CaseWorkingApiConfig> config,
    ILogger<HttpCaseWorkingApiAdapter> logger
) : ICaseWorkingApiAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CaseWorkingApiConfig _config = config.Value;

    public async Task<string> SubmitApplicationAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    )
    {
        var url = _config.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogError("CaseWorking API URL is not configured. Cannot submit work item.");
            throw new InvalidOperationException("CaseWorking API URL is not configured.");
        }

        var suffix = RandomNumberGenerator.GetInt32(1_000_000_000);
        var applicationReference = $"RA-{suffix:D9}";

        var body = new CreateWorkItemRequest
        {
            TypeId = "re-accreditation",
            Source = "operator-fe",
            ApplicationReference = applicationReference,
            Payload = BuildPayload(application),
        };

        var endpoint = $"{url.TrimEnd('/')}/work-items";
        logger.LogInformation(
            "Submitting work item to ManagementBe at {Endpoint} for applicationId={ApplicationId} org={OrganisationId}",
            endpoint,
            application.ApplicationId,
            application.OrganisationId
        );

        var userId = application.SubmittedBy?.Email ?? application.OrganisationId;
        var userName = application.SubmittedBy?.FullName;
        using var request = BuildRequest(endpoint, body, userId, userName);
        var client = httpClientFactory.CreateClient("DefaultClient");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reach ManagementBe at {Endpoint}", endpoint);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "ManagementBe returned {Status} from {Endpoint}: {Body}",
                (int)response.StatusCode,
                endpoint,
                responseBody
            );
            throw new HttpRequestException(
                $"ManagementBe work item submission failed: {(int)response.StatusCode}"
            );
        }

        var result = await response.Content.ReadFromJsonAsync<WorkItemResponseDto>(
            JsonOptions,
            cancellationToken
        );

        logger.LogInformation(
            "Work item created: workItemId={WorkItemId} applicationReference={ApplicationReference}",
            result?.Id,
            applicationReference
        );

        return applicationReference;
    }

    private static object BuildPayload(AccreditationApplicationModel application)
    {
        return new
        {
            // Hook-compatibility fields (ManagementBe hooks read these specific keys)
            organisationName = application.OrganisationName,
            registrationNumber = application.RegistrationReference,
            materialsHandled = new[] { application.MaterialType.ToString().ToLowerInvariant() },
            previousAccreditationYear = application.Year - 1,
            complianceIssuesReported = 0,
            operatorEmail = application.SubmittedBy?.Email,
            siteAddressPostcode = ExtractPostcode(application.SiteAddress),

            // Full application document
            application,
        };
    }

    internal static string? ExtractPostcode(string? siteAddress)
    {
        if (string.IsNullOrWhiteSpace(siteAddress))
            return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            siteAddress,
            @"[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.RightToLeft
        );
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private HttpRequestMessage BuildRequest(
        string url,
        CreateWorkItemRequest body,
        string? userId = null,
        string? userName = null
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };

        request.Headers.Add("x-cdp-cognito-client-id", _config.CognitoClientId);

        if (!string.IsNullOrEmpty(userId))
            request.Headers.Add("x-cdp-user-id", userId);
        if (!string.IsNullOrEmpty(userName))
            request.Headers.Add("x-cdp-user-name", userName);

        if (!string.IsNullOrEmpty(_config.SharedSecret))
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var signature = ComputeSignature(
                _config.SharedSecret,
                _config.CognitoClientId,
                userId,
                userName,
                null,
                timestamp,
                nonce
            );

            request.Headers.Add("x-cdp-auth-signature", signature);
            request.Headers.Add("x-cdp-auth-timestamp", timestamp);
            request.Headers.Add("x-cdp-auth-nonce", nonce);
        }

        return request;
    }

    // Port of ManagementBe's CognitoClientIdAuthenticationHandler.ComputeSignature
    // (v2 canonical payload). Must stay in sync — any change is a breaking change.
    internal static string ComputeSignature(
        string sharedSecret,
        string clientId,
        string? userId,
        string? userName,
        string? userRoles,
        string timestamp,
        string nonce
    )
    {
        var payload = string.Join(
            '\n',
            "v2",
            clientId,
            userId ?? string.Empty,
            userName ?? string.Empty,
            userRoles ?? string.Empty,
            timestamp,
            nonce
        );
        var keyBytes = Encoding.UTF8.GetBytes(sharedSecret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var mac = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToBase64String(mac);
    }

    #region Internal DTOs

    internal sealed class CreateWorkItemRequest
    {
        public required string TypeId { get; init; }
        public required object Payload { get; init; }
        public string? Source { get; init; }
        public string? ApplicationReference { get; init; }
    }

    internal sealed class WorkItemResponseDto
    {
        public Guid Id { get; init; }
        public string? TypeId { get; init; }
        public string? StateId { get; init; }
        public JsonElement Payload { get; init; }
    }

    #endregion
}
