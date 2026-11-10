using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using RunResult = EprRegisterEnrolBackend.AccreditationChanges.ChangeRequestItemBackfill.RunResult;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

// RA-697: change requests stored before items had their own id and status. Raw documents without those fields, as
// the pre-RA-697 code wrote them, against the real (ephemeral) server, so the backfill's own filters and updates are
// what is tested rather than the model's defaults.
public sealed class ChangeRequestItemBackfillTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MongoIntegrationFixture _fixture;
    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly IMongoCollection<BsonDocument> _raw;
    private readonly CapturingLogger<ChangeRequestItemBackfill> _logger = new();

    public ChangeRequestItemBackfillTests(MongoIntegrationFixture fixture)
    {
        _fixture = fixture;
        _databaseName = MongoIntegrationFixture.NewDatabaseName("ra697_backfill");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _raw = _factory.GetCollection<BsonDocument>(ChangeRequestPersistence.CollectionName);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private ChangeRequestItemBackfill Backfill(bool enabled = true, bool apply = true) =>
        new(
            _factory,
            Options.Create(new ChangeRequestItemBackfillOptions { Enabled = enabled, Apply = apply }),
            _logger
        );

    // A request as the pre-RA-697 code stored it: its sites carry no itemId, status or changeType.
    private static BsonDocument PreRa697Request(string id, string? status, params int[] siteIds)
    {
        var request = new BsonDocument
        {
            { "_id", id },
            { "changeType", "add-overseas-site" },
            { "organisationId", "org-1" },
            { "applicationId", $"app-{id}" },
            { "materialType", "Plastic" },
            { "year", 2026 },
            {
                "sites",
                new BsonArray(
                    siteIds.Select(siteId => new BsonDocument
                    {
                        { "siteId", siteId },
                        { "orsId", siteId.ToString("000") },
                        { "siteName", $"Site {siteId}" },
                        { "answers", new BsonDocument() },
                    })
                )
            },
            { "version", 3L },
        };
        if (status is not null)
            request["status"] = status;
        return request;
    }

    private async Task InsertAsync(params BsonDocument[] requests) =>
        await _raw.InsertManyAsync(requests.Select(r => r.DeepClone().AsBsonDocument), cancellationToken: Ct);

    private async Task<BsonDocument> RawAsync(string id) =>
        await _raw.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).SingleAsync(Ct);

    private static List<BsonDocument> Sites(BsonDocument request) =>
        request["sites"].AsBsonArray.Select(s => s.AsBsonDocument).ToList();

    [Fact]
    public async Task Disabled_ReadsAndWritesNothing()
    {
        await InsertAsync(PreRa697Request("r1", "InProgress", 1));

        var result = await Backfill(enabled: false).RunAsync(Ct);

        result.Result.Should().Be(RunResult.Disabled);
        (await RawAsync("r1")).Should().Equal(PreRa697Request("r1", "InProgress", 1));
    }

    [Fact]
    public async Task DryRun_CountsWhatItWouldBackfillAndWritesNothing()
    {
        await InsertAsync(PreRa697Request("r1", "InProgress", 1, 2), PreRa697Request("r2", "Submitted", 3));

        var result = await Backfill(apply: false).RunAsync(Ct);

        result.Should().Be(new ChangeRequestItemBackfill.Outcome(RunResult.DryRun, 2, 3, 0));
        (await RawAsync("r1")).Should().Equal(PreRa697Request("r1", "InProgress", 1, 2));
        (await RawAsync("r2")).Should().Equal(PreRa697Request("r2", "Submitted", 3));
    }

    // The items take their request's status: a draft's items are InProgress, a submitted request's Submitted. A
    // request with no status is read as InProgress, so its items are too.
    [Fact]
    public async Task Apply_GivesEachItemANewIdAndItsRequestsStatus()
    {
        await InsertAsync(
            PreRa697Request("draft", "InProgress", 1, 2),
            PreRa697Request("submitted", "Submitted", 3),
            PreRa697Request("no-status", null, 4)
        );

        var result = await Backfill().RunAsync(Ct);

        result.Should().Be(new ChangeRequestItemBackfill.Outcome(RunResult.Applied, 3, 4, 0));
        var draft = await RawAsync("draft");
        Sites(draft).Select(s => s["status"].AsString).Should().Equal("InProgress", "InProgress");
        Sites(await RawAsync("submitted")).Single()["status"].AsString.Should().Be("Submitted");
        Sites(await RawAsync("no-status")).Single()["status"].AsString.Should().Be("InProgress");
        var itemIds = (await _raw.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
            .SelectMany(Sites)
            .Select(s => s["itemId"].AsBsonBinaryData)
            .ToList();
        itemIds.Should().HaveCount(4);
        itemIds.Should().OnlyHaveUniqueItems();
        itemIds.Should().AllSatisfy(id => id.SubType.Should().Be(BsonBinarySubType.UuidStandard));
        itemIds.Should().NotContain(new BsonBinaryData(Guid.Empty, GuidRepresentation.Standard));
        // A copy read before the backfill, with its random ids, can no longer be saved over it.
        draft["version"].AsInt64.Should().Be(4);
        // Everything else about the site is left as it was.
        Sites(draft)[0]["siteName"].AsString.Should().Be("Site 1");
        Sites(draft)[0].Contains("changeType").Should().BeFalse();
    }

    // Once backfilled, the store reads the same ids every time, where before each read made new ones.
    [Fact]
    public async Task Apply_MakesTheItemIdsStableWhenRead()
    {
        await InsertAsync(PreRa697Request("r1", "Submitted", 1));
        var persistence = new ChangeRequestPersistence(_factory, NullLoggerFactory.Instance);
        async Task<AccreditationChangeRequest> ReadAsync() => (await persistence.ListAsync("org-1", "app-r1", Ct)).Single();
        (await ReadAsync()).Sites[0].ItemId.Should().NotBe((await ReadAsync()).Sites[0].ItemId);

        await Backfill().RunAsync(Ct);

        var first = await ReadAsync();
        first.Sites[0].ItemId.Should().Be((await ReadAsync()).Sites[0].ItemId);
        first.Sites[0].ItemId.Should().Be(Sites(await RawAsync("r1"))[0]["itemId"].AsGuid);
        first.Sites[0].Status.Should().Be(ChangeItemStatus.Submitted);
    }

    // Only the missing fields are filled: an item's existing id or status (a decided one, say) is kept.
    [Fact]
    public async Task Apply_KeepsAnItemsExistingIdAndStatus()
    {
        var keptId = Guid.NewGuid();
        var partial = PreRa697Request("partial", "Submitted", 1, 2, 3);
        Sites(partial)[0]["itemId"] = new BsonBinaryData(keptId, GuidRepresentation.Standard);
        Sites(partial)[1]["status"] = "Approved";
        var complete = PreRa697Request("complete", "Submitted", 4);
        Sites(complete)[0]["itemId"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard);
        Sites(complete)[0]["status"] = "Queried";
        await InsertAsync(partial, complete);

        var result = await Backfill().RunAsync(Ct);

        result.Should().Be(new ChangeRequestItemBackfill.Outcome(RunResult.Applied, 1, 3, 0));
        var sites = Sites(await RawAsync("partial"));
        sites[0]["itemId"].AsGuid.Should().Be(keptId);
        sites[0]["status"].AsString.Should().Be("Submitted");
        sites[1]["status"].AsString.Should().Be("Approved");
        sites[1].Contains("itemId").Should().BeTrue();
        sites[2]["status"].AsString.Should().Be("Submitted");
        (await RawAsync("complete")).Should().Equal(complete);
    }

    [Fact]
    public async Task Apply_IsIdempotent()
    {
        await InsertAsync(PreRa697Request("r1", "InProgress", 1, 2), PreRa697Request("r2", "Submitted", 3));
        await Backfill().RunAsync(Ct);
        var r1 = await RawAsync("r1");
        var r2 = await RawAsync("r2");

        var second = await Backfill().RunAsync(Ct);

        second.Should().Be(new ChangeRequestItemBackfill.Outcome(RunResult.Applied, 0, 0, 0));
        (await RawAsync("r1")).Should().Equal(r1);
        (await RawAsync("r2")).Should().Equal(r2);
    }

    // A request status the code does not know cannot say what its items' status is, so it is left alone and logged.
    [Fact]
    public async Task Apply_LeavesARequestWithAnUnknownStatusAlone()
    {
        await InsertAsync(PreRa697Request("odd", "Cancelled", 1));

        var result = await Backfill().RunAsync(Ct);

        result.Should().Be(new ChangeRequestItemBackfill.Outcome(RunResult.Applied, 0, 0, 1));
        (await RawAsync("odd")).Should().Equal(PreRa697Request("odd", "Cancelled", 1));
        _logger.Entries.Should().Contain(e => e.Message.Contains("odd") && e.Message.Contains("Cancelled"));
    }

    // Ships switched off, binds its Migrations section, and runs only where the change-request store is registered.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsRegisteredOnlyWithTheInYearChangesFlag_AndIsOffByDefault(bool inYearChangesEnabled)
    {
        await using var factory = new EphemeralMongoTestFactory(
            _fixture,
            "ra697_backfill_cfg",
            "Development",
            new Dictionary<string, string?>
            {
                [FeatureFlags.InYearChangesEnabledKey] = inYearChangesEnabled ? "true" : "false",
            }
        );
        using var scope = factory.Services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<ChangeRequestItemBackfillOptions>>().Value;
        options.Enabled.Should().BeFalse();
        options.Apply.Should().BeFalse();
        scope
            .ServiceProvider.GetServices<IHostedService>()
            .Count(s => s is ChangeRequestItemBackfill)
            .Should()
            .Be(inYearChangesEnabled ? 1 : 0);
    }

    [Fact]
    public async Task SectionKeysBind()
    {
        await using var factory = new EphemeralMongoTestFactory(
            _fixture,
            "ra697_backfill_cfg",
            "Development",
            new Dictionary<string, string?>
            {
                [FeatureFlags.InYearChangesEnabledKey] = "true",
                [$"{ChangeRequestItemBackfillOptions.SectionName}:Enabled"] = "true",
                [$"{ChangeRequestItemBackfillOptions.SectionName}:Apply"] = "true",
            }
        );
        using var scope = factory.Services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<ChangeRequestItemBackfillOptions>>().Value;
        options.Enabled.Should().BeTrue();
        options.Apply.Should().BeTrue();
    }
}
