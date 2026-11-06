using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

// RA-492: the Mongo store against a real (ephemeral) server - the one-draft-per-accreditation
// partial unique index and the optimistic Version check are server behaviour a fake can't prove.
public sealed class ChangeRequestPersistenceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly ChangeRequestPersistence _persistence;

    public ChangeRequestPersistenceTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("changerequests");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new ChangeRequestPersistence(_factory, NullLoggerFactory.Instance);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static AccreditationChangeRequest NewRequest(string applicationId = "app-1") =>
        new()
        {
            OrganisationId = "org-1",
            ApplicationId = applicationId,
            MaterialType = MaterialType.Plastic,
            Year = 2026,
            Sites = [new ChangeRequestSite { SiteId = 1, OrsId = "001", SiteName = "Site 1" }],
        };

    [Fact]
    public async Task InsertAsync_SecondDraftForTheSameAccreditation_IsRefused()
    {
        (await _persistence.InsertAsync(NewRequest(), Ct)).Should().BeTrue();

        (await _persistence.InsertAsync(NewRequest(), Ct)).Should().BeFalse();
        (await _persistence.InsertAsync(NewRequest("app-2"), Ct)).Should().BeTrue();
    }

    // What Submit's reopen meets if another draft was started while the request was Submitted.
    [Fact]
    public async Task UpdateAsync_ReopeningWhileAnotherDraftExists_IsRefusedAndKeepsTheVersion()
    {
        var first = NewRequest();
        await _persistence.InsertAsync(first, Ct);
        first.Status = ChangeRequestStatus.Submitted;
        await _persistence.UpdateAsync(first, Ct);
        await _persistence.InsertAsync(NewRequest(), Ct);
        var version = first.Version;

        first.Status = ChangeRequestStatus.InProgress;
        (await _persistence.UpdateAsync(first, Ct)).Should().BeFalse();

        first.Version.Should().Be(version);
        (await _persistence.ListAsync("org-1", "app-1", Ct))
            .Single(r => r.RequestId == first.RequestId)
            .Status.Should()
            .Be(ChangeRequestStatus.Submitted);
    }

    [Fact]
    public async Task InsertAsync_AfterTheDraftIsSubmitted_AllowsANewDraft()
    {
        var first = NewRequest();
        await _persistence.InsertAsync(first, Ct);
        first.Status = ChangeRequestStatus.Submitted;
        (await _persistence.UpdateAsync(first, Ct)).Should().BeTrue();

        (await _persistence.InsertAsync(NewRequest(), Ct)).Should().BeTrue();

        var all = await _persistence.ListAsync("org-1", "app-1", Ct);
        all.Should().HaveCount(2);
        all.Select(r => r.Status).Should().BeEquivalentTo([ChangeRequestStatus.InProgress, ChangeRequestStatus.Submitted]);
        (await _persistence.GetDraftAsync("org-1", "app-1", Ct))!.RequestId.Should().NotBe(first.RequestId);
    }

    [Fact]
    public async Task UpdateAsync_StaleVersion_IsRefusedAndLeavesTheStoredCopy()
    {
        var request = NewRequest();
        await _persistence.InsertAsync(request, Ct);
        var stale = (await _persistence.GetDraftAsync("org-1", "app-1", Ct))!;
        request.Sites[0].Answers.Coordinates = "51.5034, -0.1275";
        (await _persistence.UpdateAsync(request, Ct)).Should().BeTrue();

        stale.Sites[0].Answers.Country = "France";
        (await _persistence.UpdateAsync(stale, Ct)).Should().BeFalse();
        stale.Version.Should().Be(0);

        var stored = (await _persistence.GetDraftAsync("org-1", "app-1", Ct))!;
        stored.Version.Should().Be(1);
        stored.Sites[0].Answers.Coordinates.Should().Be("51.5034, -0.1275");
        stored.Sites[0].Answers.Country.Should().BeNull();
    }

    [Fact]
    public async Task DeleteDraftAsync_OnlyDeletesAnInProgressRequestAtTheVersionRead()
    {
        var request = NewRequest();
        await _persistence.InsertAsync(request, Ct);
        var stale = (await _persistence.GetDraftAsync("org-1", "app-1", Ct))!;
        await _persistence.UpdateAsync(request, Ct);

        (await _persistence.DeleteDraftAsync(stale, Ct)).Should().BeFalse();
        (await _persistence.DeleteDraftAsync(request, Ct)).Should().BeTrue();
        (await _persistence.GetDraftAsync("org-1", "app-1", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task StatusIsStoredByName()
    {
        await _persistence.InsertAsync(NewRequest(), Ct);

        var raw = await _factory
            .GetCollection<MongoDB.Bson.BsonDocument>(ChangeRequestPersistence.CollectionName)
            .Find(FilterDefinition<MongoDB.Bson.BsonDocument>.Empty)
            .SingleAsync(Ct);

        raw["status"].AsString.Should().Be("InProgress");
    }

    // RA-697: the items round-trip, with their statuses and the requested band stored by name.
    [Fact]
    public async Task ItemsRoundTripWithStatusesAndBandStoredByName()
    {
        var validFrom = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var sites = NewRequest();
        sites.Sites[0].Status = ChangeItemStatus.Approved;
        sites.Sites[0].ValidFrom = validFrom;
        var tonnage = NewRequest("app-2");
        tonnage.ChangeType = AccreditationChangeRequest.UpgradeTonnageBandChangeType;
        tonnage.Sites = [];
        tonnage.TonnageBandUpgrade = new TonnageBandUpgradeItem
        {
            Status = ChangeItemStatus.DulyMade,
            RequestedBand = PlannedTonnageBand.Over10000,
        };
        await _persistence.InsertAsync(sites, Ct);
        await _persistence.InsertAsync(tonnage, Ct);

        var storedSite = (await _persistence.GetDraftAsync("org-1", "app-1", Ct))!.Sites.Single();
        storedSite.ItemId.Should().Be(sites.Sites[0].ItemId);
        storedSite.ChangeType.Should().Be(AccreditationChangeRequest.AddOverseasSiteChangeType);
        storedSite.Status.Should().Be(ChangeItemStatus.Approved);
        storedSite.ValidFrom.Should().Be(validFrom);
        storedSite.WithdrawnAt.Should().BeNull();
        var storedTonnage = (await _persistence.GetDraftAsync("org-1", "app-2", Ct))!;
        storedTonnage.Sites.Should().BeEmpty();
        storedTonnage.TonnageBandUpgrade.Should().BeEquivalentTo(tonnage.TonnageBandUpgrade);

        var raw = await _factory
            .GetCollection<MongoDB.Bson.BsonDocument>(ChangeRequestPersistence.CollectionName)
            .Find(Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("applicationId", "app-2"))
            .SingleAsync(Ct);
        raw["tonnageBandUpgrade"]["status"].AsString.Should().Be("DulyMade");
        raw["tonnageBandUpgrade"]["requestedBand"].AsString.Should().Be("Over10000");
        raw["tonnageBandUpgrade"]["changeType"].AsString.Should().Be("upgrade-tonnage-band");
    }
}
