using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.TestData;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// RA-706, the cut: with <c>DomainModel:LegacyWrites</c> off, each write route goes through the handlers as before,
/// but the change reaches the model alone. The legacy document keeps the version and timestamp it had before the
/// write, and no staged copy is left behind.
/// </summary>
public sealed partial class DualWriteEndpointTests
{
    private Task<long> StagedCopiesAsync() =>
        _factory
            .Services.GetRequiredService<IMongoDbClientFactory>()
            .GetCollection<BsonDocument>(DomainModelCollections.CutStagingApplications)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

    [Theory]
    [InlineData("update-site")]
    [InlineData("promote-site")]
    [InlineData("revert-site")]
    [InlineData("patch-sites")]
    [InlineData("update-interim")]
    [InlineData("withdraw-interim")]
    [InlineData("restore-interim")]
    [InlineData("add-file")]
    [InlineData("delete-file")]
    [InlineData("accreditation-number")]
    public async Task Cut_WriteRoute_WritesTheModelAndLeavesLegacyAlone(string route)
    {
        var application = await MigratedEveryRouteWritableAsync();
        var modelBefore = (await CurrentApplicationAsync(application))!.Version;
        var legacyBefore = (await LegacyAsync(application))!;
        var (cutFactory, cutClient) = Alongside(
            s_cut,
            services =>
                services.Replace(ServiceDescriptor.Singleton(ScannedUpload("file-2", "extra.pdf")))
        );
        using var _ = cutFactory;
        using var __ = cutClient;

        var response = await WriteAsync(application, route, cutClient);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{route}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}"
        );
        Assert.True((await CurrentApplicationAsync(application))!.Version > modelBefore);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal(legacyBefore.Version, legacy.Version);
        Assert.Equal(legacyBefore.UpdatedAt, legacy.UpdatedAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, await StagedCopiesAsync());
    }

    [Fact]
    public async Task Cut_AddSite_WritesTheModelAndLeavesLegacyAlone()
    {
        var application = await MigratedEveryRouteWritableAsync();
        var legacyBefore = (await LegacyAsync(application))!;
        var (cutFactory, cutClient) = Alongside(s_cut);
        using var _ = cutFactory;
        using var __ = cutClient;

        var response = await cutClient.PostAsJsonAsync(
            Url(application),
            AddRequest("Cut Site"),
            Ct
        );

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        Assert.Contains((await CurrentSitesAsync()), s => s.SiteName == "Cut Site");
        Assert.Equal(legacyBefore.Version, (await LegacyAsync(application))!.Version);
        Assert.Equal(0, await StagedCopiesAsync());
    }

    // A read after a cut write comes from the model, so the client sees its own change.
    [Fact]
    public async Task Cut_ReadAfterWrite_ShowsTheChange()
    {
        var application = await MigratedEveryRouteWritableAsync();
        var (cutFactory, cutClient) = Alongside(s_cut);
        using var _ = cutFactory;
        using var __ = cutClient;

        await WriteAsync(application, "update-site", cutClient);
        var read = await JsonAsync(
            await cutClient.GetAsync(
                $"/api/v1/accreditation-applications/{OrganisationId}/{application.Id}",
                Ct
            )
        );

        var site = read.GetProperty("overseasSites")
            .GetProperty("sites")
            .EnumerateArray()
            .Single(s => s.GetProperty("siteId").GetInt32() == 2);
        Assert.Equal("Renamed", site.GetProperty("siteName").GetString());
    }

    // After the cut the model is the only copy, so a write's response is read back from it once the unit of work has
    // committed: the same content, version and updatedAt the next read serves, not the handler's working copy.
    [Theory]
    [InlineData("patch-sites")]
    [InlineData("accreditation-number")]
    public async Task Cut_WriteResponse_IsWhatTheNextReadServes(string route)
    {
        var application = await MigratedEveryRouteWritableAsync();
        var (cutFactory, cutClient) = Alongside(s_cut);
        using var _ = cutFactory;
        using var __ = cutClient;

        var written = await WriteAsync(application, route, cutClient);
        Assert.True(written.IsSuccessStatusCode, $"{route}: {written.StatusCode}");
        var response = ToMilliseconds(await NodeAsync(written));
        var next = ToMilliseconds(
            await NodeAsync(await GetAsync(cutClient, ByIdPath(application)))
        );

        Assert.True(
            JsonNode.DeepEquals(next, response),
            $"{route}: the response {response} is not the next read {next}"
        );
    }

    // The cut finds every live application already in the model; a no-op unit of work on the dual-write host migrates
    // one built in legacy.
    private async Task MigrateAsync(AccreditationApplicationModel application)
    {
        var migrated = await _factory
            .Services.GetRequiredService<IDualWriteUnitOfWork>()
            .RunAsync(
                "Test",
                application.Id!.Value.ToString(),
                (_, _) => Task.FromResult(Microsoft.AspNetCore.Http.Results.Ok()),
                Ct
            );
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(migrated);
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("resubmit")]
    [InlineData("withdraw")]
    [InlineData("status-duly-made")]
    [InlineData("status-rejected")]
    [InlineData("status-approved")]
    [InlineData("query")]
    [InlineData("registration-number")]
    [InlineData("recycling-operations")]
    [InlineData("add-interim")]
    public async Task Cut_StatusNumberOrAddRoute_WritesTheModelAndLeavesLegacyAlone(string route)
    {
        ReExKnowsNothing();
        var application = await WithWasteProcessingTypeAsync(route);
        await MigrateAsync(application);
        var modelBefore = (await CurrentApplicationAsync(application))!.Version;
        var legacyBefore = (await LegacyAsync(application))!;
        var (cutFactory, cutClient) = Alongside(s_cut);
        using var _ = cutFactory;
        using var __ = cutClient;

        var response = await StatusNumberOrAddWriteAsync(route, application, cutClient);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{route}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}"
        );
        Assert.True((await CurrentApplicationAsync(application))!.Version > modelBefore);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal(legacyBefore.Version, legacy.Version);
        Assert.Equal(legacyBefore.UpdatedAt, legacy.UpdatedAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, await StagedCopiesAsync());
    }

    // Seed after the cut leaves the model as seed did before it (see
    // Seed_DualWriteSeedOn_WritesTheEntitiesAndTheInitialApplicationVersionOverHttp), and writes no legacy document.
    [Fact]
    public async Task Cut_Seed_WritesTheSameModelAsBeforeTheCutAndNoLegacy()
    {
        Restart(s_cut);
        ReExHoldsTheRegistration();

        var created = await SeededApplicationAsync(await SeedAsync());

        var registration = (await CurrentRegistrationAsync())!;
        Assert.Equal("R26ER5000270001ST", registration.RegistrationNumber);
        Assert.Equal(Nation.England, registration.Nation);
        Assert.Equal("2 Mill Rd, Leeds", registration.Site!.FormattedAddress);
        Assert.Equal(["EPR/AB1234"], registration.PermitNumbers);
        var ors = Assert.Single(await SeededSitesAsync());
        Assert.Equal("001", ors.OrsId);
        Assert.Equal(registration.EntityId, ors.RegistrationId);
        var entity = (await CurrentApplicationAsync(created))!;
        Assert.Equal(1, entity.Version);
        Assert.Equal(registration.EntityId, entity.RegistrationId);
        Assert.Equal(
            ors.EntityId,
            Assert.Single(entity.AsAccreditation().ApplicationOverseasSites).OverseasSiteId
        );
        Assert.Equal(2027, created.Year);
        Assert.Equal(0, await CountAsync(DomainModelCollections.LegacyApplications));
        Assert.Equal(0, await StagedCopiesAsync());
    }

    [Fact]
    public async Task Cut_FirstWriteAfterTheSeed_VersionsTheSeedsApplicationOnItsRegistration()
    {
        Restart(s_cut);
        ReExHoldsTheRegistration();
        var created = await SeededApplicationAsync(await SeedAsync());
        var registration = (await CurrentRegistrationAsync())!;

        var response = await _client.PatchAsJsonAsync(
            AppUrl(created, "business-plan"),
            new { newInfrastructurePercent = 10, newInfrastructureDetail = "A new baler" },
            Ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entity = (await CurrentApplicationAsync(created))!;
        Assert.Equal(2, entity.Version);
        Assert.Equal(registration.EntityId, entity.RegistrationId);
        Assert.Equal(registration.EntityId, (await CurrentRegistrationAsync())!.EntityId);
        Assert.Equal(0, await CountAsync(DomainModelCollections.LegacyApplications));
    }

    // RA-646's fixed test accreditations, seeded at every startup in dev and test: after the cut a redeploy seeds them
    // into the model again, and a reset (teardown, then seed) leaves exactly them, read from the model.
    [Fact]
    public async Task Cut_TestDataSeedReseedAndReset_ServeTheScenariosFromTheModel()
    {
        Restart(s_cut);
        var seeder = _factory.Services.GetRequiredService<AccreditationApiTestDataSeeder>();

        await seeder.SeedAsync();
        await seeder.SeedAsync();
        await seeder.ResetAsync();

        foreach (var scenario in AccreditationApiTestData.All)
        {
            var read = await Applications.GetByIdAsync(
                scenario.OrgId.ToString(),
                scenario.Id.ToString()
            );
            Assert.True(read is not null, $"scenario {scenario.OrgId} is not served");
            Assert.Equal(scenario.Build().Year, read!.Year);
        }
        Assert.Equal(AccreditationApiTestData.All.Count, await CountCurrentApplicationsAsync());
        Assert.Equal(0, await CountAsync(DomainModelCollections.LegacyApplications));
        Assert.Equal(0, await StagedCopiesAsync());
    }

    private Task<long> CountCurrentApplicationsAsync() =>
        _factory
            .Services.GetRequiredService<IMongoDbClientFactory>()
            .GetCollection<BsonDocument>(DomainModelCollections.ApplicationVersions)
            .CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("isCurrent", true),
                cancellationToken: Ct
            );
}
