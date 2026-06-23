using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.ReEx.Config;
using EprRegisterEnrolBackend.ReEx.Dtos;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.ReEx;

public class ReExClient : IReExClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ReExClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // Required: the ReEx API does not guarantee that the polymorphic discriminator
        // ("wasteProcessingType") is the first property in each registration object.
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ReExClient(HttpClient httpClient, IOptions<ReExConfig> config, ILogger<ReExClient> logger)
    {
        _logger = logger;
        var baseUrl = config.Value.BaseUrl;
        if (!string.IsNullOrWhiteSpace(baseUrl))
            httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + '/');
        _httpClient = httpClient;
    }

    public async Task<ReExResult<OrganisationDto>> GetOrganisationsAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                $"v1/organisations/{Uri.EscapeDataString(organisationId)}",
                cancellationToken
            );
            return await MapResponseAsync<OrganisationDto>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("ReEx API request timed out for organisation {OrganisationId}", organisationId);
            return ReExResult<OrganisationDto>.Fail(new ReExError(ReExErrorKind.Timeout, "Request timed out"));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Transport error calling ReEx API for organisation {OrganisationId}", organisationId);
            return ReExResult<OrganisationDto>.Fail(new ReExError(ReExErrorKind.TransportError, "Transport error"));
        }
    }

    public async Task<ReExResult<OverseasSitesDto>> GetOverseasSiteAsync(
        string organisationId,
        string registrationId,
        string accreditationId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                $"v1/organisations/{Uri.EscapeDataString(organisationId)}" +
                $"/registrations/{Uri.EscapeDataString(registrationId)}" +
                $"/accreditations/{Uri.EscapeDataString(accreditationId)}/overseas-sites",
                cancellationToken
            );
            return await MapResponseAsync<OverseasSitesDto>(response, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "ReEx API request timed out for overseas sites (org={OrganisationId} reg={RegistrationId} acc={AccreditationId})",
                organisationId, registrationId, accreditationId
            );
            return ReExResult<OverseasSitesDto>.Fail(new ReExError(ReExErrorKind.Timeout, "Request timed out"));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "Transport error calling ReEx API for overseas sites (org={OrganisationId} reg={RegistrationId} acc={AccreditationId})",
                organisationId, registrationId, accreditationId
            );
            return ReExResult<OverseasSitesDto>.Fail(new ReExError(ReExErrorKind.TransportError, "Transport error"));
        }
    }

    private async Task<ReExResult<T>> MapResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        var statusCode = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
                if (value is null)
                    return ReExResult<T>.Fail(
                        new ReExError(ReExErrorKind.DeserializationError, "Response body was empty"),
                        statusCode
                    );
                return ReExResult<T>.Success(value, statusCode);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to deserialize ReEx API response (status {StatusCode})", statusCode);
                return ReExResult<T>.Fail(
                    new ReExError(ReExErrorKind.DeserializationError, "Invalid response body"),
                    statusCode
                );
            }
        }

        return statusCode switch
        {
            401 or 403 => ReExResult<T>.Fail(
                new ReExError(ReExErrorKind.AuthError, $"Authentication failed ({statusCode})"),
                statusCode
            ),
            404 => ReExResult<T>.Fail(
                new ReExError(ReExErrorKind.NotFound, "Resource not found"),
                statusCode
            ),
            >= 400 and < 500 => ReExResult<T>.Fail(
                new ReExError(ReExErrorKind.ClientError, $"Client error ({statusCode})"),
                statusCode
            ),
            _ => ReExResult<T>.Fail(
                new ReExError(ReExErrorKind.ServerError, $"Server error ({statusCode})"),
                statusCode
            ),
        };
    }
}
