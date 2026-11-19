using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.DomainModel.Cut;

/// <summary>
/// RA-794's isNewSite audit and correction after the cut: the window is read from the model's createdAt (v6.14), and
/// the correction's write goes through the cut's unit of work like any other.
/// </summary>
public sealed partial class ModelBackedApplicationPersistenceTests
{
    [Fact]
    public async Task GetCreatedBeforeWithOverseasSites_FindsTheSameApplications()
    {
        await LookupDataAsync();
        await MigratedAsync(
            Variant(
                Day1,
                a =>
                {
                    a.RegistrationId = OtherRegistrationId;
                    a.RegistrationReference = "R27EX0000001P";
                    a.OverseasSites = new AccreditationApplicationOverseasSites { Sites = [] };
                },
                OtherOrganisationId
            )
        );
        var everything = await _legacy.GetCreatedBeforeWithOverseasSitesAsync(DateTime.MaxValue, Ct);
        var cut = everything.Select(a => a.CreatedAt).Order().ElementAt(everything.Count / 2);

        var legacy = await _legacy.GetCreatedBeforeWithOverseasSitesAsync(cut, Ct);
        var model = await _model.GetCreatedBeforeWithOverseasSitesAsync(cut, Ct);

        Assert.NotEmpty(legacy);
        Assert.True(legacy.Count < everything.Count, "the window must leave some applications out");
        Assert.Equal(Ids(legacy), Ids(model));
    }

    // A version the backfill missed cannot be placed inside or outside the window: refused, never skipped.
    [Fact]
    public async Task GetCreatedBeforeWithOverseasSites_CurrentVersionWithoutCreatedAt_Refuses()
    {
        var legacy = await MigratedAsync();
        await _factory
            .GetClient()
            .GetDatabase(_databaseName)
            .GetCollection<BsonDocument>(DomainModelCollections.ApplicationVersions)
            .UpdateManyAsync(
                Builders<BsonDocument>.Filter.Eq("entityId", legacy.Id!.Value),
                Builders<BsonDocument>.Update.Unset("createdAt"),
                cancellationToken: Ct
            );

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _model.GetCreatedBeforeWithOverseasSitesAsync(DateTime.MaxValue, Ct)
        );

        Assert.Contains(legacy.Id!.Value.ToString(), refused.Message);
    }

    [Fact]
    public async Task IsNewSiteCorrection_AfterTheCut_CorrectsTheModelAndLeavesLegacyAlone()
    {
        var application = NewApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Carried Over Ltd", IsNewSite = true }],
        };
        var migrated = await MigratedAsync(application);
        var reEx = Substitute.For<IReExApiAdapter>();
        reEx.GetAccreditationAsync(ReExOrganisationId, ReExRegistrationId, MaterialType.Plastic, 2026)
            .Returns(
                ReExResult<ReExAccreditationDto>.Success(
                    new ReExAccreditationDto
                    {
                        OverseasSites =
                        [
                            new OverseasSiteModel { SiteId = 1, SiteName = "Carried Over Ltd", OrsId = "001" },
                        ],
                    },
                    200
                )
            );
        var services = new ServiceCollection()
            .AddSingleton<IAccreditationApplicationPersistence>(_model)
            .AddSingleton(reEx)
            .AddSingleton<IDualWriteUnitOfWork>(CutUnitOfWork())
            .AddSingleton(_stores)
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [IsNewSiteConfig.CorrectKey] = "true",
                    [IsNewSiteConfig.SpotCheckConfirmedByKey] = "tom.halley",
                    [IsNewSiteConfig.ApplyKey] = "true",
                    [IsNewSiteConfig.DeployedAtKey] = "2026-11-01T00:00:00Z",
                }
            )
            .Build();
        var log = new CapturingLogger<IsNewSiteCorrectionMigration>();
        var proposal = (await CurrentVersionAsync(migrated))!.AsAccreditation().ApplicationOverseasSites.Single();
        Assert.Equal(
            SiteOrigin.Operator,
            (
                await _stores.OverseasSites.Versions.FindCurrentAsync(
                    Builders<OverseasSiteEntity>.Filter.Eq(o => o.EntityId, proposal.OverseasSiteId!.Value),
                    null,
                    Ct
                )
            )!.Origin
        );

        var result = await new IsNewSiteCorrectionMigration(services, configuration, log).RunAsync(Ct);

        Assert.Equal(IsNewSiteCorrectionMigration.RunResult.Applied, result);
        Assert.DoesNotContain(log.Entries, e => e.LogLevel == LogLevel.Error);
        var model = (await ReadThroughModelAsync(migrated.Id!.Value))!;
        Assert.False(model.OverseasSites!.Sites.Single().IsNewSite);
        var entry = (await CurrentVersionAsync(migrated))!.AsAccreditation().ApplicationOverseasSites.Single();
        Assert.False(entry.IsNewSite);
        var ors = (
            await _stores.OverseasSites.Versions.FindCurrentAsync(
                Builders<OverseasSiteEntity>.Filter.Eq(o => o.EntityId, entry.OverseasSiteId!.Value),
                null,
                Ct
            )
        )!;
        Assert.Equal(SiteOrigin.ReEx, ors.Origin);
        Assert.Equal(OverseasSiteStatus.Active, ors.Status);
        Assert.Null(ors.ProposedByApplicationId);
        var legacy = (await _legacy.GetByIdAsync(migrated.OrganisationId, migrated.Id!.Value.ToString()))!;
        Assert.True(legacy.OverseasSites!.Sites.Single().IsNewSite);
        Assert.Equal(0, await StagedCountAsync());
    }
}
