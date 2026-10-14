using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationChanges;

public sealed class ChangeRequestSubmission(IHttpClientFactory clients,
    IOptions<CaseWorkingApiConfig> options, ChangeRequestPersistence persistence)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    public async Task<ChangeRequest> Submit(ChangeRequest request, CancellationToken ct)
    {
        if (request.CaseId is not null) return request;
        var config = options.Value;
        if (config.UseStub || string.IsNullOrWhiteSpace(config.SharedSecret))
            throw new HttpRequestException("The spike requires signed, real management ingestion.");
        var payload = new
        {
            operatorRequestId = request.RequestId,
            request.SchemaVersion,
            request.Target,
            request.Proposal,
            request.ProposedSiteId,
            request.BaselineSites,
            operatorOrganisationId = request.Target.OrganisationId,
            request.OrganisationName,
            request.RegistrationReference,
            material = request.Target.MaterialType.ToString(),
            accreditationYear = request.Target.Year,
            wasteProcessingType = "exporter",
        };
        using var message = CaseWorkingRequestFactory.Create(config, HttpMethod.Post,
            config.Url.TrimEnd('/') + "/work-items", new { typeId = request.ChangeType, source = "operator-fe", payload },
            request.Target.OrganisationId, contentOptions: JsonOptions);
        using var response = await clients.CreateClient("DefaultClient").SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(ct);
        var receipt = body?.RootElement ?? throw new HttpRequestException("Missing management receipt.");
        if (!receipt.TryGetProperty("id", out var id) || !Guid.TryParse(id.GetString(), out var caseId)
            || caseId == Guid.Empty || receipt.GetProperty("typeId").GetString() != request.ChangeType
            || !receipt.TryGetProperty("applicationReference", out var reference)
            || string.IsNullOrWhiteSpace(reference.GetString())
            || receipt.GetProperty("payload").GetProperty("operatorRequestId").GetString() != request.RequestId
            || receipt.GetProperty("payload").GetProperty("proposedSiteId").GetString() != request.ProposedSiteId)
            throw new HttpRequestException("Unconfirmed management receipt correlation.");
        var receivedTarget = receipt.GetProperty("payload").GetProperty("target");
        if (receivedTarget.GetProperty("organisationId").GetString() != request.Target.OrganisationId
            || receivedTarget.GetProperty("registrationId").GetString() != request.Target.RegistrationId
            || receivedTarget.GetProperty("accreditationId").GetString() != request.Target.AccreditationId
            || receivedTarget.GetProperty("materialType").GetString() != request.Target.MaterialType.ToString()
            || receivedTarget.GetProperty("year").GetInt32() != request.Target.Year)
            throw new HttpRequestException("Unconfirmed management target correlation.");
        return await persistence.ConfirmReceipt(request, caseId.ToString(), reference.GetString()!, ct);
    }
}
