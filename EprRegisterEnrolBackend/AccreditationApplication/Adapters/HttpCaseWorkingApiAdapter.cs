using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public class HttpCaseWorkingApiAdapter(
    HttpClient httpClient,
    IOptions<CaseWorkingApiConfig> config,
    ILogger<HttpCaseWorkingApiAdapter> logger) : ICaseWorkingApiAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string?> SubmitApplicationAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default)
    {
        var cfg = config.Value;

        var body = new
        {
            typeId = "re-accreditation",
            source = "epr-register-enrol-backend",
            payload = new
            {
                organisationName = application.OrganisationName,
                registrationNumber = application.RegistrationId,
                materialsHandled = new[] { application.MaterialType.ToString() },
                siteAddress = application.SiteAddress,
                siteAddressPostcode = ExtractPostcode(application.SiteAddress),
                accreditationYear = application.Year,
                operatorApplicationId = application.ApplicationId,
                submittedBy = application.SubmittedBy is null ? null : new
                {
                    fullName = application.SubmittedBy.FullName,
                    jobTitle = application.SubmittedBy.JobTitle,
                    email = application.SubmittedBy.Email
                },
                prns = new
                {
                    plannedTonnageBand = application.Prns.PlannedTonnageBand?.ToString(),
                    authorisers = application.Prns.Authorisers.Select(a => new
                    {
                        fullName = a.FullName,
                        email = a.Email
                    }).ToArray()
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
                    newUsesDetail = application.BusinessPlan.NewUsesDetail
                },
                samplingPlan = new
                {
                    files = application.SamplingPlan.Files.Select(f => new
                    {
                        filename = f.Filename,
                        uploadedAt = f.UploadedAt,
                        scanStatus = f.ScanStatus.ToString()
                    }).ToArray()
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/work-items")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };

        AddAuthHeaders(request, cfg);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to reach case management backend for applicationId={ApplicationId}",
                application.ApplicationId);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Case management backend returned {StatusCode} for applicationId={ApplicationId}: {Body}",
                (int)response.StatusCode, application.ApplicationId, errorBody);
            response.EnsureSuccessStatusCode();
        }

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        if (doc.RootElement.TryGetProperty("payload", out var payload) &&
            payload.TryGetProperty("applicationReference", out var refEl) &&
            refEl.ValueKind == JsonValueKind.String)
        {
            var reference = refEl.GetString();
            logger.LogInformation(
                "Work item created in case management for applicationId={ApplicationId} caseRef={CaseRef}",
                application.ApplicationId, reference);
            return reference;
        }

        logger.LogWarning(
            "Case management work item created but applicationReference missing from response for applicationId={ApplicationId}",
            application.ApplicationId);
        return null;
    }

    private static void AddAuthHeaders(HttpRequestMessage request, CaseWorkingApiConfig cfg)
    {
        request.Headers.Add("x-cdp-cognito-client-id", cfg.ClientId);
        // Service-to-service submissions have no human actor; use the service
        // client ID as a system identity so the audit trail remains attributable.
        request.Headers.Add("x-cdp-user-id", cfg.ClientId);

        if (string.IsNullOrEmpty(cfg.SharedSecret))
            return;

        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var signature = ComputeSignature(cfg.SharedSecret, cfg.ClientId, timestamp, nonce);

        request.Headers.Add("x-cdp-auth-timestamp", timestamp);
        request.Headers.Add("x-cdp-auth-nonce", nonce);
        request.Headers.Add("x-cdp-auth-signature", signature);
    }

    // Canonical signing payload (v2) — mirrors CognitoClientIdAuthenticationHandler.ComputeSignature.
    // Service-to-service calls carry no end-user identity, so userId/userName/roles are empty strings.
    private static string ComputeSignature(string secret, string clientId, string timestamp, string nonce)
    {
        var payload = string.Join('\n', "v2", clientId, string.Empty, string.Empty, string.Empty, timestamp, nonce);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(mac);
    }

    private static string? ExtractPostcode(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            address,
            @"[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.RightToLeft);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }
}
