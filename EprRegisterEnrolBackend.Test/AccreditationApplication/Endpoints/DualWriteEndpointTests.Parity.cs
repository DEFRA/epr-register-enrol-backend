using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// RA-705: with <c>DomainModel:ReadFromModel</c> on, every route in scope must answer exactly the JSON it answers with
/// the flag off. Two apps run over the same database, one with the flag off and one with it on, so each comparison is
/// of the same data served from the two sources.
/// </summary>
public sealed partial class DualWriteEndpointTests
{
    private static async Task<JsonNode> NodeAsync(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}"
        );
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    // A write's response carries the time it set in memory (to the tick); Mongo stores milliseconds, so a read
    // afterwards shows the stored value. That is true with the flag off too, so compare to the millisecond.
    private static readonly Regex s_fraction = new(@"\.(\d+)Z", RegexOptions.Compiled);

    private static JsonNode ToMilliseconds(JsonNode node) =>
        JsonNode.Parse(
            s_fraction.Replace(
                node.ToJsonString(),
                m => "." + m.Groups[1].Value.PadRight(3, '0')[..3] + "Z"
            )
        )!;

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path) =>
        client.GetAsync(path, Ct);

    private static string ByIdPath(AccreditationApplicationModel application) =>
        $"/api/v1/accreditation-applications/{OrganisationId}/{application.Id}";

    private const string ListPath = $"/api/v1/accreditation-applications/{OrganisationId}";

    // The fixture every write route can act on, migrated by a first dual-written write through the flag-off app.
    private async Task<AccreditationApplicationModel> MigratedEveryRouteWritableAsync()
    {
        ReExKnowsNothing();
        var application = await EveryRouteWritableAsync();
        // A seeded application always carries its waste processing type (Seed takes it from ReEx). The model's
        // Registration can't hold "none", so a legacy document without one is served with one: the readiness checks
        // list any such document before the flag goes on.
        application.WasteProcessingType = "exporter";
        application = (await Applications.UpdateAsync(application))!;
        var migrated = await _client.PatchAsync(
            AppUrl(application, "prns"),
            new StringContent(
                """{"plannedTonnageBand":"UpTo500"}""",
                System.Text.Encoding.UTF8,
                "application/json"
            ),
            Ct
        );
        Assert.Equal(HttpStatusCode.OK, migrated.StatusCode);
        return application;
    }

    private static IEnumerable<JsonNode> Descendants(JsonNode node)
    {
        yield return node;
        var children = node switch
        {
            JsonObject o => o.Select(p => p.Value),
            JsonArray a => a.AsEnumerable(),
            _ => [],
        };
        foreach (var child in children.OfType<JsonNode>())
        {
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    [Fact]
    public async Task Parity_GetById_SameJsonFromBothSources()
    {
        var application = await MigratedEveryRouteWritableAsync();
        var (onFactory, on) = Alongside(s_readFromModelOn);
        using var _ = onFactory;

        var fromLegacy = await NodeAsync(await GetAsync(_client, ByIdPath(application)));
        var fromModel = await NodeAsync(await GetAsync(on, ByIdPath(application)));

        Assert.True(
            JsonNode.DeepEquals(fromLegacy, fromModel),
            $"legacy {fromLegacy}\nmodel {fromModel}"
        );
    }

    [Fact]
    public async Task Parity_GetList_SameJsonFromBothSources()
    {
        await MigratedEveryRouteWritableAsync();
        var (onFactory, on) = Alongside(s_readFromModelOn);
        using var _ = onFactory;

        var fromLegacy = await NodeAsync(await GetAsync(_client, ListPath));
        var fromModel = await NodeAsync(await GetAsync(on, ListPath));

        Assert.Single(fromLegacy.AsArray());
        Assert.True(
            JsonNode.DeepEquals(fromLegacy, fromModel),
            $"legacy {fromLegacy}\nmodel {fromModel}"
        );
    }

    // A write through the flag-on app answers from the model; the flag-off app then reads the same state from legacy.
    // A whole-application response must equal that GET, and a site or interim site response must equal its element in
    // it, field for field.
    [Theory]
    [InlineData("update-site")]
    [InlineData("promote-site")]
    [InlineData("revert-site")]
    [InlineData("patch-sites")]
    [InlineData("update-interim")]
    [InlineData("withdraw-interim")]
    [InlineData("restore-interim")]
    [InlineData("delete-file")]
    [InlineData("accreditation-number")]
    public async Task Parity_WriteResponse_SameJsonAsLegacyAfterTheWrite(string route)
    {
        var application = await MigratedEveryRouteWritableAsync();
        var (onFactory, on) = Alongside(s_readFromModelOn);
        using var _ = onFactory;

        var written = await WriteAsync(application, route, on);
        Assert.True(written.IsSuccessStatusCode, $"{route}: {(int)written.StatusCode}");
        if ((await written.Content.ReadAsStringAsync(Ct)).Length == 0)
        {
            return; // no body (204, or a bare 200): nothing to serve from either source
        }

        await AssertServedAsLegacyAsync(
            route,
            application,
            written,
            ResponseBody.ApplicationOrElement
        );
    }

    public enum ResponseBody
    {
        None,
        Application,
        Element,
        ApplicationOrElement,

        // Not built from the application (Submit's receipt): it must equal the flag-off app's answer to the same
        // write on an identical fixture. Its reference is unique per application, so each is checked against its own.
        SameAsFlagOff,
    }

    // The response equals the flag-off app's GET after the write: the whole application, or one element of it
    // (a site or an interim site), field for field.
    private async Task AssertServedAsLegacyAsync(
        string route,
        AccreditationApplicationModel application,
        HttpResponseMessage written,
        ResponseBody expected
    )
    {
        var body = await written.Content.ReadAsStringAsync(Ct);
        if (expected == ResponseBody.None)
        {
            Assert.True(body.Length == 0, $"{route}: expected no body, got {body}");
            return;
        }

        var response = ToMilliseconds(await NodeAsync(written));
        var legacy = ToMilliseconds(
            await NodeAsync(await GetAsync(_client, ByIdPath(application)))
        );
        var matches = expected switch
        {
            ResponseBody.Application => JsonNode.DeepEquals(legacy, response),
            ResponseBody.Element => Descendants(legacy)
                .Skip(1)
                .Any(node => JsonNode.DeepEquals(node, response)),
            _ => Descendants(legacy).Any(node => JsonNode.DeepEquals(node, response)),
        };
        Assert.True(matches, $"{route}: response {response}\nis not {expected} in legacy {legacy}");
    }

    // The status changes, the registration number, recycling operations and the two adds, each on the fixture its own
    // dual-write test uses, written through the flag-on app. The write migrates the application in its transaction.
    [Theory]
    [InlineData("submit", ResponseBody.SameAsFlagOff)]
    [InlineData("resubmit", ResponseBody.Application)]
    [InlineData("withdraw", ResponseBody.Application)]
    [InlineData("status-duly-made", ResponseBody.Application)]
    [InlineData("status-rejected", ResponseBody.Application)]
    [InlineData("status-approved", ResponseBody.Application)]
    [InlineData("query", ResponseBody.Application)]
    [InlineData("registration-number", ResponseBody.Application)]
    [InlineData("recycling-operations", ResponseBody.Element)]
    [InlineData("add-site", ResponseBody.Element)]
    [InlineData("add-interim", ResponseBody.Element)]
    public async Task Parity_StatusNumberOrAddResponse_SameJsonAsLegacyAfterTheWrite(
        string route,
        ResponseBody expected
    )
    {
        ReExKnowsNothing();
        var application = await WithWasteProcessingTypeAsync(route);
        var (onFactory, on) = Alongside(s_readFromModelOn);
        using var _ = onFactory;

        var written = await StatusNumberOrAddWriteAsync(route, application, on);

        Assert.True(
            written.IsSuccessStatusCode,
            $"{route}: {(int)written.StatusCode} {await written.Content.ReadAsStringAsync(Ct)}"
        );
        if (expected != ResponseBody.SameAsFlagOff)
        {
            await AssertServedAsLegacyAsync(route, application, written, expected);
            return;
        }

        var flagOn = await ReceiptWithoutItsReferenceAsync(written, application);
        var other = await WithWasteProcessingTypeAsync(route);
        _caseWorking
            .SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new CaseWorkingSubmissionResult("ACC-REF-2", Guid.NewGuid()));
        var flagOff = await ReceiptWithoutItsReferenceAsync(
            await StatusNumberOrAddWriteAsync(route, other, _client),
            other
        );
        Assert.True(
            JsonNode.DeepEquals(flagOff, flagOn),
            $"{route}: flag off {flagOff}\nflag on {flagOn}"
        );
    }

    private async Task<JsonNode> ReceiptWithoutItsReferenceAsync(
        HttpResponseMessage response,
        AccreditationApplicationModel application
    )
    {
        var receipt = await NodeAsync(response);
        Assert.Equal(
            (await LegacyAsync(application))!.ApplicationReference,
            receipt["accreditationReference"]!.GetValue<string>()
        );
        receipt.AsObject().Remove("accreditationReference");
        return receipt;
    }

    private async Task<AccreditationApplicationModel> WithWasteProcessingTypeAsync(string route)
    {
        var application = await StatusNumberOrAddFixtureAsync(route);
        // Seed always sets it; the model can't hold "none" (see MigratedEveryRouteWritableAsync).
        application.WasteProcessingType = application.IsExporter ? "exporter" : "reprocessor";
        return (await Applications.UpdateAsync(application))!;
    }

    private async Task<AccreditationApplicationModel> StatusNumberOrAddFixtureAsync(string route)
    {
        switch (route)
        {
            case "submit":
                CaseManagementAcceptsSubmissions(Guid.NewGuid());
                return await ReadyToSubmitAsync();
            case "resubmit":
                return await QueriedAsync();
            case "withdraw" or "status-duly-made" or "status-rejected" or "query":
                return (await SubmittedWithProposedSiteAsync()).Application;
            case "status-approved":
                return (await ReadyToApproveAsync()).Application;
            case "registration-number":
                return await NumberedApplicationAsync(ApplicationStatus.Started, 2027);
            case "recycling-operations" or "add-site" or "add-interim":
                return await ApplicationAsync(null, ReExSite());
            default:
                throw new ArgumentOutOfRangeException(nameof(route), route, null);
        }
    }

    private Task<HttpResponseMessage> StatusNumberOrAddWriteAsync(
        string route,
        AccreditationApplicationModel application,
        HttpClient client
    ) =>
        route switch
        {
            "submit" => SubmitAsync(application, client),
            "resubmit" => ResubmitAsync(application, client),
            "withdraw" => WithdrawAsync(application, client),
            "status-duly-made" => StatusPushAsync(application, "duly-made", client),
            "status-rejected" => StatusPushAsync(application, "rejected", client),
            "status-approved" => StatusPushAsync(application, "approved", client),
            "query" => QueryPushAsync(application, client),
            "registration-number" => client.PostAsJsonAsync(
                NumberUrl(application, "registration"),
                s_registrationNumberBody,
                Ct
            ),
            "recycling-operations" => PatchRecyclingOperationsAsync(application, client),
            "add-site" => client.PostAsJsonAsync(Url(application), AddRequest(), Ct),
            "add-interim" => client.PostAsJsonAsync(InterimUrl(application), InterimRequest(), Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };

    [Fact]
    public async Task Parity_PrnsResponse_SameJsonAsLegacyAfterTheWrite()
    {
        var application = await MigratedEveryRouteWritableAsync();
        var (onFactory, on) = Alongside(s_readFromModelOn);
        using var _ = onFactory;

        var response = ToMilliseconds(await NodeAsync(await PatchPrnsAsync(application, on)));
        var legacy = ToMilliseconds(
            await NodeAsync(await GetAsync(_client, ByIdPath(application)))
        );

        Assert.True(JsonNode.DeepEquals(legacy, response), $"legacy {legacy}\nresponse {response}");
    }
}
