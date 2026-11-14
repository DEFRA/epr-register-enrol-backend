using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using EprRegisterEnrolBackend.AccreditationApplication.Models;

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
            s_fraction.Replace(node.ToJsonString(), m => "." + m.Groups[1].Value.PadRight(3, '0')[..3] + "Z")
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

        var response = ToMilliseconds(await NodeAsync(written));
        var legacy = ToMilliseconds(await NodeAsync(await GetAsync(_client, ByIdPath(application))));

        Assert.True(
            Descendants(legacy).Any(node => JsonNode.DeepEquals(node, response)),
            $"{route}: response {response}\nis not the application or any element of it in legacy {legacy}"
        );
    }

    [Fact]
    public async Task Parity_PrnsResponse_SameJsonAsLegacyAfterTheWrite()
    {
        var application = await MigratedEveryRouteWritableAsync();
        var (onFactory, on) = Alongside(s_readFromModelOn);
        using var _ = onFactory;

        var response = ToMilliseconds(await NodeAsync(await PatchPrnsAsync(application, on)));
        var legacy = ToMilliseconds(await NodeAsync(await GetAsync(_client, ByIdPath(application))));

        Assert.True(JsonNode.DeepEquals(legacy, response), $"legacy {legacy}\nresponse {response}");
    }
}
