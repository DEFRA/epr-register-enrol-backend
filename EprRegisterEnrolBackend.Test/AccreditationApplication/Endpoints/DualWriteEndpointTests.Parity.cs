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

    // Several applications, end to end: the model keeps legacy's order, leaves out one it doesn't hold (decided
    // 2026-11-13, no legacy fallback), and serves one with no waste processing type with the type its Registration holds
    // (the known gap readiness check E lists). Apart from those two, every field is the same.
    [Fact]
    public async Task Parity_GetList_SeveralApplications_SameOrderWithTheKnownDifferencesOnly()
    {
        var withType = await MigratedEveryRouteWritableAsync();
        var withoutType = await ApplicationAsync();
        Assert.Null(withoutType.WasteProcessingType);
        // A seed takes the registration reference from ReEx, so a second application in the group carries it too.
        withoutType.RegistrationReference = (await LegacyAsync(withType))!.RegistrationReference;
        withoutType = (await Applications.UpdateAsync(withoutType))!;
        var migrated = await _client.PatchAsync(
            AppUrl(withoutType, "prns"),
            new StringContent(
                """{"plannedTonnageBand":"UpTo500"}""",
                System.Text.Encoding.UTF8,
                "application/json"
            ),
            Ct
        );
        Assert.Equal(HttpStatusCode.OK, migrated.StatusCode);
        // Another registration's group, never written, so the model has never held it.
        var missing = await ApplicationAsync();
        missing.RegistrationId = "reex-registration-never-written";
        missing = (await Applications.UpdateAsync(missing))!;
        var (onFactory, on) = Alongside(s_readFromModelOn);
        using var _ = onFactory;

        var fromLegacy = await NodeAsync(await GetAsync(_client, ListPath));
        var fromModel = await NodeAsync(await GetAsync(on, ListPath));

        var legacyIds = fromLegacy
            .AsArray()
            .Select(a => a!["applicationId"]!.GetValue<string>())
            .ToList();
        Assert.Equal(3, legacyIds.Count);
        Assert.Contains(missing.Id.ToString(), legacyIds);
        var expected = new JsonArray([
            .. fromLegacy
                .AsArray()
                .Where(a => a!["applicationId"]!.GetValue<string>() != missing.Id.ToString())
                .Select(a => a!.DeepClone()),
        ]);
        var servedWithoutType = fromModel
            .AsArray()
            .Single(a => a!["applicationId"]!.GetValue<string>() == withoutType.Id.ToString())!;
        Assert.Equal("exporter", servedWithoutType["wasteProcessingType"]!.GetValue<string>());
        var expectedWithoutType = expected.Single(a =>
            a!["applicationId"]!.GetValue<string>() == withoutType.Id.ToString()
        )!;
        Assert.Null(expectedWithoutType["wasteProcessingType"]);
        expectedWithoutType["wasteProcessingType"] = "exporter";
        Assert.Contains(withType.Id.ToString(), legacyIds);
        Assert.True(
            JsonNode.DeepEquals(expected, fromModel),
            $"expected {expected}\nmodel {fromModel}"
        );
    }

    // A write through the flag-on app answers from the model; the flag-off app then reads the same state from legacy.
    // Each route names exactly what its response is: the whole application, the one site or interim site the route
    // writes (found by its ids, not by shape, so another site of the same shape can't pass), or no body at all.
    [Theory]
    [InlineData("update-site", ResponseBody.Element, 2, null)]
    [InlineData("promote-site", ResponseBody.Element, 1, null)]
    [InlineData("revert-site", ResponseBody.Element, 3, null)]
    [InlineData("patch-sites", ResponseBody.Application, null, null)]
    [InlineData("update-interim", ResponseBody.Element, 1, 1)]
    [InlineData("withdraw-interim", ResponseBody.None, null, null)]
    [InlineData("restore-interim", ResponseBody.Element, 1, 2)]
    [InlineData("delete-file", ResponseBody.None, null, null)]
    [InlineData("accreditation-number", ResponseBody.Application, null, null)]
    public async Task Parity_WriteResponse_SameJsonAsLegacyAfterTheWrite(
        string route,
        ResponseBody expected,
        int? siteId,
        int? interimSiteId
    )
    {
        var application = await MigratedEveryRouteWritableAsync();
        var (onFactory, on) = Alongside(s_readFromModelOn);
        using var _ = onFactory;

        var written = await WriteAsync(application, route, on);
        Assert.True(written.IsSuccessStatusCode, $"{route}: {(int)written.StatusCode}");

        await AssertServedAsLegacyAsync(
            route,
            application,
            written,
            expected,
            siteId,
            interimSiteId
        );
    }

    public enum ResponseBody
    {
        None,
        Application,
        Element,

        // Not built from the application (Submit's receipt): it must equal the flag-off app's answer to the same
        // write on an identical fixture. Its reference is unique per application, so each is checked against its own.
        SameAsFlagOff,
    }

    // The site with the given id in an application's JSON, or that site's interim site with the given id.
    private static JsonNode? ElementOf(JsonNode application, int siteId, int? interimSiteId)
    {
        var site = application["overseasSites"]
            ?["sites"]?.AsArray()
            .FirstOrDefault(s => s?["siteId"]?.GetValue<int>() == siteId);
        return interimSiteId is null
            ? site
            : site
                ?["interimSites"]?.AsArray()
                .FirstOrDefault(i => i?["siteId"]?.GetValue<int>() == interimSiteId);
    }

    // The response equals the flag-off app's GET after the write: the whole application, or one element of it
    // (a site or an interim site), field for field. An element named by its ids must be that element; one without
    // (an add, whose id the write assigns) must be some element of the application other than its root.
    private async Task AssertServedAsLegacyAsync(
        string route,
        AccreditationApplicationModel application,
        HttpResponseMessage written,
        ResponseBody expected,
        int? siteId = null,
        int? interimSiteId = null
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
            ResponseBody.Element when siteId is not null => JsonNode.DeepEquals(
                ElementOf(legacy, siteId.Value, interimSiteId)
                    ?? throw new InvalidOperationException(
                        $"{route}: site {siteId} interim {interimSiteId} is not in legacy {legacy}"
                    ),
                response
            ),
            ResponseBody.Element => Descendants(legacy)
                .Skip(1)
                .Any(node => JsonNode.DeepEquals(node, response)),
            _ => throw new ArgumentOutOfRangeException(nameof(expected), expected, null),
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
