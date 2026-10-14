using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationChanges;

public sealed class ChangeRequestCaseReader(IHttpClientFactory clients, IOptions<CaseWorkingApiConfig> options,
    ILogger<ChangeRequestCaseReader> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task ReadState(ChangeRequest? request, CancellationToken ct)
    {
        if (request?.CaseId is null) return;
        var config = options.Value;
        try
        {
            if (config.UseStub || string.IsNullOrWhiteSpace(config.SharedSecret)) return;
            using var message = CaseWorkingRequestFactory.Create(config, HttpMethod.Get,
                config.Url.TrimEnd('/') + "/work-items/" + Uri.EscapeDataString(request.CaseId));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await clients.CreateClient("DefaultClient").SendAsync(message, timeout.Token);
            response.EnsureSuccessStatusCode();
            var item = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            var payload = item.GetProperty("payload");
            var target = payload.GetProperty("target").Deserialize<AccreditationChangeTarget>(JsonOptions);
            if (item.GetProperty("id").GetString() != request.CaseId
                || item.GetProperty("typeId").GetString() != request.ChangeType
                || payload.GetProperty("operatorRequestId").GetString() != request.RequestId
                || payload.GetProperty("proposedSiteId").GetString() != request.ProposedSiteId
                || target != request.Target)
                throw new HttpRequestException("Management case correlation does not match this request.");
            var state = item.GetProperty("stateId").GetString();
            if (state is "submitted" or "assessment-in-progress" or "approved" or "rejected" or "withdrawn")
                request.CaseState = state;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
            or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Current management state unavailable for change request {RequestId}", request.RequestId);
        }
    }
}
