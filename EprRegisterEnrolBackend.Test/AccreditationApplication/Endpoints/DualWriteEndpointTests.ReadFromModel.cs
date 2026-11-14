using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// RA-705, phase 5: with <c>DomainModel:ReadFromModel</c> on, GET by id and GET list come from the domain model through
/// <see cref="IApplicationReader"/>. To tell the two sources apart, each test changes legacy alone after the migration,
/// so the response shows which one it was read from.
/// </summary>
public sealed partial class DualWriteEndpointTests
{
    private const string ModelName = "Acme Recycling Ltd";
    private const string LegacyOnlyName = "Changed In Legacy Only";

    private static readonly Dictionary<string, string?> s_readFromModelOn = new(s_dualWriteOn)
    {
        ["DomainModel:ReadFromModel"] = "true",
    };

    // Migrated by a dual-written write, then renamed in legacy alone (no sync runs for a raw edit).
    // Each restart starts a fresh database, so a test switches ReadFromModel on before it builds its data.
    private async Task<AccreditationApplicationModel> MigratedThenRenamedInLegacyAsync()
    {
        var application = await ApplicationAsync(null, ReExSite());
        var added = await _client.PostAsJsonAsync(Url(application), AddRequest(), Ct);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        await _factory
            .Services.GetRequiredService<IMongoDbClientFactory>()
            .GetCollection<BsonDocument>(LegacyCollection)
            .UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", application.Id!.Value),
                Builders<BsonDocument>.Update.Set("organisationName", LegacyOnlyName),
                cancellationToken: Ct
            );
        return application;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private Task<HttpResponseMessage> GetListAsync() =>
        _client.GetAsync($"/api/v1/accreditation-applications/{OrganisationId}", Ct);

    [Fact]
    public void ReadFromModel_On_ResolvesTheModelReader()
    {
        Restart(s_readFromModelOn);

        Assert.IsType<ModelApplicationReader>(
            _factory.Services.GetRequiredService<IApplicationReader>()
        );
    }

    [Fact]
    public void ReadFromModel_Off_ResolvesTheLegacyReader() =>
        Assert.IsType<LegacyApplicationReader>(
            _factory.Services.GetRequiredService<IApplicationReader>()
        );

    [Fact]
    public async Task ReadFromModel_On_GetById_ServesTheModel()
    {
        Restart(s_readFromModelOn);
        ReExKnowsNothing();
        var application = await MigratedThenRenamedInLegacyAsync();

        var body = await JsonAsync(await GetByIdAsync(application));

        Assert.Equal(ModelName, body.GetProperty("organisationName").GetString());
    }

    [Fact]
    public async Task ReadFromModel_Off_GetById_ServesLegacy()
    {
        ReExKnowsNothing();
        var application = await MigratedThenRenamedInLegacyAsync();

        var body = await JsonAsync(await GetByIdAsync(application));

        Assert.Equal(LegacyOnlyName, body.GetProperty("organisationName").GetString());
    }

    [Fact]
    public async Task ReadFromModel_On_GetById_TakesTheVersionFromLegacy()
    {
        Restart(s_readFromModelOn);
        ReExKnowsNothing();
        var application = await MigratedThenRenamedInLegacyAsync();

        var body = await JsonAsync(await GetByIdAsync(application));

        Assert.Equal(
            (await LegacyAsync(application))!.Version,
            body.GetProperty("version").GetInt64()
        );
    }

    // Decided 2026-11-13: after the migration an application missing from the model exists nowhere; no legacy fallback.
    [Fact]
    public async Task ReadFromModel_On_GetById_NotInTheModel_Answers404()
    {
        Restart(s_readFromModelOn);
        ReExKnowsNothing();
        var legacyOnly = await ApplicationAsync(null, ReExSite());

        var response = await GetByIdAsync(legacyOnly);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReadFromModel_On_GetList_ServesTheModel()
    {
        Restart(s_readFromModelOn);
        ReExKnowsNothing();
        var application = await MigratedThenRenamedInLegacyAsync();

        var list = await JsonAsync(await GetListAsync());

        var only = Assert.Single(list.EnumerateArray());
        Assert.Equal(application.Id.ToString(), only.GetProperty("applicationId").GetString());
        Assert.Equal(ModelName, only.GetProperty("organisationName").GetString());
    }

    // The flag changes where the application is read from, not GetById's backfills: they still run and still persist.
    [Fact]
    public async Task ReadFromModel_On_GetById_StillBackfillsTheNation()
    {
        Restart(s_readFromModelOn);
        var application = await MigratedWithoutNationOrOrgIdAsync();
        ReExKnowsNothing();
        _reEx
            .GetNationAsync(OrganisationId, ReExRegistrationId, Arg.Any<CancellationToken>())
            .Returns(ReExResult<Nation>.Success(Nation.Wales, 200));

        var body = await JsonAsync(await GetByIdAsync(application));

        Assert.Equal(Nation.Wales, (await RegistrationOfAsync(application)).Nation);
        Assert.Equal("Wales", body.GetProperty("nation").GetString());
    }

    // A write's response is built inside the dual-write transaction, before the sync reaches the model, so with
    // ReadFromModel on it is served again through the reader once the write has committed.
    private Task<HttpResponseMessage> PatchPrnsAsync(AccreditationApplicationModel application) =>
        _client.PatchAsync(
            AppUrl(application, "prns"),
            new StringContent(
                """{"plannedTonnageBand":"UpTo500"}""",
                System.Text.Encoding.UTF8,
                "application/json"
            ),
            Ct
        );

    [Fact]
    public async Task ReadFromModel_On_WriteResponse_ServesTheModel()
    {
        Restart(s_readFromModelOn);
        ReExKnowsNothing();
        var application = await MigratedThenRenamedInLegacyAsync();

        var body = await JsonAsync(await PatchPrnsAsync(application));

        Assert.Equal(ModelName, body.GetProperty("organisationName").GetString());
        Assert.Equal("UpTo500", body.GetProperty("prns").GetProperty("plannedTonnageBand").GetString());
    }

    [Fact]
    public async Task ReadFromModel_On_WriteResponse_TakesTheVersionFromLegacy()
    {
        Restart(s_readFromModelOn);
        ReExKnowsNothing();
        var application = await MigratedThenRenamedInLegacyAsync();

        var body = await JsonAsync(await PatchPrnsAsync(application));

        Assert.Equal(
            (await LegacyAsync(application))!.Version,
            body.GetProperty("version").GetInt64()
        );
    }

    [Fact]
    public async Task ReadFromModel_Off_WriteResponse_ServesLegacy()
    {
        ReExKnowsNothing();
        var application = await MigratedThenRenamedInLegacyAsync();

        var body = await JsonAsync(await PatchPrnsAsync(application));

        Assert.Equal(LegacyOnlyName, body.GetProperty("organisationName").GetString());
    }

    // Read after dual-write, on a replica set: a change written in one request is served from the model by the next.
    [Fact]
    public async Task ReadFromModel_On_GetById_AfterAWrite_ServesTheWrittenChangeFromTheModel()
    {
        Restart(s_readFromModelOn);
        ReExKnowsNothing();
        var application = await MigratedThenRenamedInLegacyAsync();
        Assert.Equal(HttpStatusCode.OK, (await PatchPrnsAsync(application)).StatusCode);

        var body = await JsonAsync(await GetByIdAsync(application));

        Assert.Equal("UpTo500", body.GetProperty("prns").GetProperty("plannedTonnageBand").GetString());
        Assert.Equal(ModelName, body.GetProperty("organisationName").GetString());
        Assert.Equal((await LegacyAsync(application))!.Version, body.GetProperty("version").GetInt64());
    }

    // A seed writes the initial Application version, so with ReadFromModel on its Created response is served from the
    // model. Write responses are strict like reads: a 404 here would mean the seed left the model without it.
    [Fact]
    public async Task ReadFromModel_On_Seed_AnswersCreatedFromTheModel()
    {
        Restart(new(s_readFromModelOn) { ["DomainModel:DualWriteSeed"] = "true" });
        ReExHoldsTheRegistration();

        var response = await SeedAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(response.Headers.Location!.OriginalString.Split('/')[^1], body.GetProperty("applicationId").GetString());
    }
}
