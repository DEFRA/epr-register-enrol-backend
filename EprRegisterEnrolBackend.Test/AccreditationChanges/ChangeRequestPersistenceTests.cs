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
}
