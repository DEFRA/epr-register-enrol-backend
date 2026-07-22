using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public async Task<CaseWorkingSubmissionResult> SubmitApplicationAsync(
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

        // RA-318: ManagementBe owns applicationReference generation — it is never
        // supplied by the caller (ManagementBe ignores any value sent here) and is
        // read back from its response below rather than generated locally.
        var body = new CreateWorkItemRequest
        {
            TypeId = "re-accreditation",
            Source = "operator-fe",
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
        using var request = BuildRequest(HttpMethod.Post, endpoint, body, userId, userName);
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

        WorkItemResponseDto? result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<WorkItemResponseDto>(
                JsonOptions,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            // Unlike workItemId below, applicationReference has no local fallback — it is
            // ManagementBe-generated, so a response we can't parse means we have no valid
            // reference to persist and the submission must fail rather than proceed silently.
            logger.LogError(
                ex,
                "Failed to parse ManagementBe work item response from {Endpoint}; cannot obtain application reference",
                endpoint
            );
            throw new HttpRequestException(
                $"Failed to parse ManagementBe work item response from {endpoint}.",
                ex
            );
        }

        if (string.IsNullOrWhiteSpace(result?.ApplicationReference))
        {
            logger.LogError(
                "ManagementBe response from {Endpoint} did not include an application reference",
                endpoint
            );
            throw new HttpRequestException(
                $"ManagementBe work item response from {endpoint} did not include an application reference."
            );
        }

        // Guid.Empty means the "id" field was absent from the response body (not a parse
        // failure — System.Text.Json leaves missing value-type properties at their default).
        // Unlike applicationReference, workItemId is only ever an optional correlation aid,
        // so a missing id must not fail the submission.
        Guid? workItemId = result.Id == Guid.Empty ? null : result.Id;

        logger.LogInformation(
            "Work item created: workItemId={WorkItemId} applicationReference={ApplicationReference}",
            workItemId,
            result.ApplicationReference
        );

        return new CaseWorkingSubmissionResult(result.ApplicationReference, workItemId);
    }

    public async Task<string?> GetNotificationStatusAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    )
    {
        if (application.CaseManagementWorkItemId is not { } workItemId)
        {
            return null;
        }

        var url = _config.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogWarning(
                "CaseWorking API URL is not configured. Cannot look up notification status for workItemId={WorkItemId}.",
                workItemId
            );
            return null;
        }

        var endpoint = $"{url.TrimEnd('/')}/work-items/{workItemId}";

        try
        {
            var userId = application.SubmittedBy?.Email ?? application.OrganisationId;
            var userName = application.SubmittedBy?.FullName;
            using var request = BuildRequest(
                HttpMethod.Get,
                endpoint,
                userId: userId,
                userName: userName
            );
            var client = httpClientFactory.CreateClient("DefaultClient");

            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "ManagementBe returned {Status} from {Endpoint}; notification status will not be captured",
                    (int)response.StatusCode,
                    endpoint
                );
                return null;
            }

            var detail = await response.Content.ReadFromJsonAsync<WorkItemDetailResponseDto>(
                JsonOptions,
                cancellationToken
            );
            return NotificationStatusResolver.Resolve(detail?.AuditLog);
        }
        catch (Exception ex)
        {
            // Must never fail the caller's GetById response — the work item id is only ever
            // an optional correlation aid, not something the operator's own data depends on.
            logger.LogWarning(
                ex,
                "Failed to look up notification status from ManagementBe at {Endpoint}",
                endpoint
            );
            return null;
        }
    }

    private static object BuildPayload(AccreditationApplicationModel application)
    {
        return new
        {
            organisationName = application.OrganisationName,
            registrationNumber = application.RegistrationReference,
            materialsHandled = new[] { application.MaterialType.ToString().ToLowerInvariant() },
            glassRecyclingProcess = application.GlassRecyclingProcess,
            material = application.MaterialType.ToString().ToLowerInvariant(),
            accreditationYear = application.Year,
            previousAccreditationYear = application.Year - 1,
            complianceIssuesReported = 0,
            siteAddress = application.SiteAddress,
            siteAddressPostcode = ExtractPostcode(application.SiteAddress),
            operatorApplicationId = application.ApplicationId,
            operatorOrganisationId = application.OrganisationId,
            operatorRegistrationId = application.RegistrationId,
            operatorEmail = application.SubmittedBy?.Email,
            submittedBy = application.SubmittedBy is null
                ? null
                : new
                {
                    fullName = application.SubmittedBy.FullName,
                    jobTitle = application.SubmittedBy.JobTitle,
                    email = application.SubmittedBy.Email,
                },
            prns = new
            {
                plannedTonnageBand = application.Prns.PlannedTonnageBand?.ToString(),
                authorisers = application
                    .Prns.Authorisers.Select(a => new { fullName = a.FullName, email = a.Email })
                    .ToArray(),
            },
            businessPlan = new
            {
                newInfrastructurePercent = application.BusinessPlan.NewInfrastructurePercent,
                priceSupportPercent = application.BusinessPlan.PriceSupportPercent,
                businessCollectionsPercent = application.BusinessPlan.BusinessCollectionsPercent,
                communicationsPercent = application.BusinessPlan.CommunicationsPercent,
                newMarketsPercent = application.BusinessPlan.NewMarketsPercent,
                newUsesPercent = application.BusinessPlan.NewUsesPercent,
                newInfrastructureDetail = application.BusinessPlan.NewInfrastructureDetail,
                priceSupportDetail = application.BusinessPlan.PriceSupportDetail,
                businessCollectionsDetail = application.BusinessPlan.BusinessCollectionsDetail,
                communicationsDetail = application.BusinessPlan.CommunicationsDetail,
                newMarketsDetail = application.BusinessPlan.NewMarketsDetail,
                newUsesDetail = application.BusinessPlan.NewUsesDetail,
            },
            samplingPlan = new
            {
                files = application
                    .SamplingPlan.Files.Select(f => new
                    {
                        fileId = f.FileId,
                        filename = f.Filename,
                        contentType = f.ContentType,
                        uploadedAt = f.UploadedAt,
                        scanStatus = f.ScanStatus.ToString(),
                        s3Key = f.S3Key,
                        s3Bucket = f.S3Bucket,
                    })
                    .ToArray(),
            },
            overseasSites = new
            {
                sites = (application.OverseasSites?.Sites ?? [])
                    .Select(s => new
                    {
                        siteId = s.SiteId,
                        siteName = s.SiteName,
                        siteAddress = s.SiteAddress,
                        country = s.Country,
                        besEvidence = new
                        {
                            files = (s.BesEvidence?.BesEvidenceUploads ?? [])
                                .Select(f => new
                                {
                                    fileId = f.FileId,
                                    filename = f.Filename,
                                    contentType = f.ContentType,
                                    uploadedAt = f.UploadedAt,
                                    scanStatus = f.ScanStatus,
                                    besEvidenceValidFromDate = f.BesEvidenceValidFromDate,
                                    besEvidenceExpiryDate = f.BesEvidenceExpiryDate,
                                    s3Key = f.S3Key,
                                    s3Bucket = f.S3Bucket,
                                })
                                .ToArray(),
                        },
                    })
                    .ToArray(),
            },
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
        HttpMethod method,
        string url,
        object? body = null,
        string? userId = null,
        string? userName = null
    )
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

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
    // (v3 canonical payload — see ManagementBe ADR-0005). Must stay in sync —
    // any change is a breaking change requiring a coordinated deploy.
    internal static string ComputeSignature(
        string sharedSecret,
        string clientId,
        string? userId,
        string? userName,
        string timestamp,
        string nonce
    )
    {
        var payload = string.Join(
            '\n',
            "v3",
            clientId,
            userId ?? string.Empty,
            userName ?? string.Empty,
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
    }

    internal sealed class WorkItemResponseDto
    {
        public Guid Id { get; init; }
        public string? TypeId { get; init; }
        public string? StateId { get; init; }
        public JsonElement Payload { get; init; }
        public string? ApplicationReference { get; init; }
    }

    #endregion
}
