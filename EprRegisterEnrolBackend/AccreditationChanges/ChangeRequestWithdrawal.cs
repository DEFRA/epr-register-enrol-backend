using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationChanges;

public sealed class ChangeRequestWithdrawal(IHttpClientFactory clients, IOptions<CaseWorkingApiConfig> options,
    ChangeRequestPersistence persistence)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<bool> Withdraw(ChangeRequest request, CancellationToken ct)
    {
        var config = options.Value;
        if (config.UseStub || string.IsNullOrWhiteSpace(config.SharedSecret))
            throw new HttpRequestException("Signed management withdrawal is not configured.");
        await persistence.SetWithdrawalPending(request.RequestId, true, ct);
        using var message = CaseWorkingRequestFactory.Create(config, HttpMethod.Post,
            config.Url.TrimEnd('/') + "/work-items/add-overseas-site/" + Uri.EscapeDataString(request.CaseId!) + "/withdraw",
            new { requestId = request.RequestId, proposedSiteId = request.ProposedSiteId, target = request.Target },
            userId: request.Target.OrganisationId, contentOptions: JsonOptions);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await clients.CreateClient("DefaultClient").SendAsync(message, timeout.Token);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            await persistence.SetWithdrawalPending(request.RequestId, false, ct);
            return false;
        }
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        if (ChangeRequestCaseReader.ReadCorrelatedState(receipt, request) != "withdrawn")
            throw new HttpRequestException("Management withdrawal is unconfirmed.");
        return await persistence.RecordDecision(new(request.RequestId, request.CaseId!, request.ProposedSiteId,
            request.Target, "withdrawn"), ct);
    }
}