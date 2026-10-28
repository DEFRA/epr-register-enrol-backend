using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Test.TestSupport;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Versioning;

public sealed class VersionedMongoRepositoryTests : IDisposable
{
    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly TestClock _clock = new(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
    private readonly VersionedMongoRepository<WidgetVersion> _sut;

    public VersionedMongoRepositoryTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("versioned");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        var collection = _factory.GetCollection<WidgetVersion>("widgetVersions");
        collection.Indexes.CreateMany(VersionedIndexes.Identity<WidgetVersion>());
        collection.Indexes.CreateOne(
            VersionedIndexes.CurrentUnique<WidgetVersion>(
                "code_current_u",
                Builders<WidgetVersion>.IndexKeys.Ascending(x => x.Code)
            )
        );
        _sut = new VersionedMongoRepository<WidgetVersion>(
            _factory.GetClient(),
            collection,
            _clock
        );
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChangeInfo Change(ChangeReason reason = ChangeReason.Migration) =>
        new() { Reason = reason };

    private static WidgetVersion Widget(string name, ObjectId? entityId = null) =>
        new()
        {
            EntityId = entityId ?? ObjectId.Empty,
            Code = "A",
            Name = name,
        };

    [Fact]
    public async Task InsertAsync_NewEntity_WritesVersionOneAsCurrent()
    {
        var created = await _sut.InsertAsync(Widget("first"), Change(), cancellationToken: Ct);

        Assert.NotEqual(ObjectId.Empty, created.EntityId);
        Assert.NotEqual(created.Id, created.EntityId);
        Assert.Equal(1, created.Version);
        Assert.True(created.IsCurrent);
        Assert.Equal(_clock.UtcNow.UtcDateTime, created.ValidFrom);
        Assert.Equal(VersionSentinel.ValidTo, created.ValidTo);
        var read = await _sut.GetCurrentAsync(created.EntityId, cancellationToken: Ct);
        Assert.Equal("first", read!.Name);
    }

    [Fact]
    public async Task InsertAsync_AnyEntity_LeavesTheCallersObjectUnchanged()
    {
        var proposed = Widget("first");

        var created = await _sut.InsertAsync(proposed, Change(), cancellationToken: Ct);

        Assert.NotSame(proposed, created);
        Assert.Equal(ObjectId.Empty, proposed.Id);
        Assert.Equal(ObjectId.Empty, proposed.EntityId);
        Assert.Equal(0, proposed.Version);
        Assert.False(proposed.IsCurrent);
    }

    [Fact]
    public async Task UpdateAsync_ExpectedVersionIsCurrent_ClosesItAndInsertsTheNext()
    {
        var v1 = await _sut.InsertAsync(Widget("first"), Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromHours(1));

        var result = await _sut.UpdateAsync(
            Widget("second", v1.EntityId),
            expectedVersion: 1,
            Change(ChangeReason.Suspended),
            cancellationToken: Ct
        );

        Assert.True(result.Changed);
        var versions = await _sut.GetVersionsAsync(v1.EntityId, cancellationToken: Ct);
        Assert.Equal([1L, 2L], versions.Select(v => v.Version));
        Assert.Equal([false, true], versions.Select(v => v.IsCurrent));
        Assert.Equal(versions[1].ValidFrom, versions[0].ValidTo);
        Assert.Equal(VersionSentinel.ValidTo, versions[1].ValidTo);
        Assert.Equal(ChangeReason.Suspended, versions[1].LastChange.Reason);
        Assert.Equal(v1.EntityId, versions[1].EntityId);
        Assert.NotEqual(versions[0].Id, versions[1].Id);
    }

    [Fact]
    public async Task UpdateAsync_StaleExpectedVersion_ThrowsConflictAndWritesNothing()
    {
        var v1 = await _sut.InsertAsync(Widget("first"), Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _sut.UpdateAsync(Widget("second", v1.EntityId), 1, Change(), cancellationToken: Ct);

        await Assert.ThrowsAsync<VersionConflictException>(() =>
            _sut.UpdateAsync(Widget("third", v1.EntityId), 1, Change(), cancellationToken: Ct)
        );

        Assert.Equal(2, (await _sut.GetVersionsAsync(v1.EntityId, cancellationToken: Ct)).Count);
    }

    [Fact]
    public async Task UpdateAsync_TwoWritersFromTheSameVersion_OneWinsAndOneConflicts()
    {
        var v1 = await _sut.InsertAsync(Widget("first"), Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var attempts = Enumerable
            .Range(0, 2)
            .Select(i =>
                Task.Run(async () =>
                {
                    try
                    {
                        await _sut.UpdateAsync(
                            Widget($"writer {i}", v1.EntityId),
                            1,
                            Change(),
                            cancellationToken: Ct
                        );
                        return true;
                    }
                    catch (VersionConflictException)
                    {
                        return false;
                    }
                })
            )
            .ToArray();
        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(1, outcomes.Count(won => won));
        var versions = await _sut.GetVersionsAsync(v1.EntityId, cancellationToken: Ct);
        Assert.Equal([1L, 2L], versions.Select(v => v.Version));
        Assert.Single(versions, v => v.IsCurrent);
    }

    [Fact]
    public async Task UpdateAsync_WritesInTheSameMillisecond_GetDistinctOrderedPeriods()
    {
        var v1 = await _sut.InsertAsync(Widget("first"), Change(), cancellationToken: Ct);

        // The clock does not move, so both updates land in the insert's millisecond.
        await _sut.UpdateAsync(Widget("second", v1.EntityId), 1, Change(), cancellationToken: Ct);
        await _sut.UpdateAsync(Widget("third", v1.EntityId), 2, Change(), cancellationToken: Ct);

        var versions = await _sut.GetVersionsAsync(v1.EntityId, cancellationToken: Ct);
        Assert.Equal(3, versions.Count);
        Assert.All(versions.Take(2), v => Assert.True(v.ValidFrom < v.ValidTo));
        Assert.True(versions[0].ValidFrom < versions[1].ValidFrom);
        Assert.True(versions[1].ValidFrom < versions[2].ValidFrom);
        Assert.Equal(versions[2].ValidFrom, versions[1].ValidTo);
    }

    [Fact]
    public async Task GetAsOfAsync_AnyInstant_ReturnsTheVersionCurrentThen()
    {
        var t0 = _clock.UtcNow.UtcDateTime;
        var v1 = await _sut.InsertAsync(Widget("first"), Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromHours(2));
        await _sut.UpdateAsync(Widget("second", v1.EntityId), 1, Change(), cancellationToken: Ct);

        Assert.Equal(
            "first",
            (await _sut.GetAsOfAsync(v1.EntityId, t0.AddHours(1), cancellationToken: Ct))!.Name
        );
        Assert.Equal(
            "second",
            (await _sut.GetAsOfAsync(v1.EntityId, t0.AddHours(3), cancellationToken: Ct))!.Name
        );
        Assert.Null(await _sut.GetAsOfAsync(v1.EntityId, t0.AddMinutes(-1), cancellationToken: Ct));
        // Periods are half-open: at the instant of the change, the new version applies.
        Assert.Equal(
            "second",
            (await _sut.GetAsOfAsync(v1.EntityId, t0.AddHours(2), cancellationToken: Ct))!.Name
        );
    }

    [Fact]
    public async Task UpdateAsync_NothingChanged_WritesNoVersion()
    {
        var v1 = await _sut.InsertAsync(Widget("first"), Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await _sut.UpdateAsync(
            Widget("first", v1.EntityId),
            1,
            Change(ChangeReason.ReExRefresh),
            cancellationToken: Ct
        );

        Assert.False(result.Changed);
        Assert.Equal(1, result.Entity.Version);
        Assert.Single(await _sut.GetVersionsAsync(v1.EntityId, cancellationToken: Ct));
    }

    [Fact]
    public async Task UpdateAsync_OnlyTheImportTimeChanged_WritesNoVersion()
    {
        var first = Widget("first");
        first.Source = new EntitySource
        {
            System = SourceSystem.ReEx,
            ImportedAt = new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc),
        };
        var v1 = await _sut.InsertAsync(
            first,
            Change(ChangeReason.ReExImport),
            cancellationToken: Ct
        );
        _clock.Advance(TimeSpan.FromDays(1));

        var refreshed = Widget("first", v1.EntityId);
        refreshed.Source = new EntitySource
        {
            System = SourceSystem.ReEx,
            ImportedAt = new DateTime(2027, 1, 2, 9, 0, 0, DateTimeKind.Utc),
        };
        var result = await _sut.UpdateAsync(
            refreshed,
            1,
            Change(ChangeReason.ReExRefresh),
            cancellationToken: Ct
        );

        Assert.False(result.Changed);
        Assert.Single(await _sut.GetVersionsAsync(v1.EntityId, cancellationToken: Ct));
    }

    [Fact]
    public async Task UpdateAsync_OtherSourceFieldChanged_WritesAVersion()
    {
        var first = Widget("first");
        first.Source = new EntitySource { System = SourceSystem.ReEx, ReExSchemaVersion = "1" };
        var v1 = await _sut.InsertAsync(
            first,
            Change(ChangeReason.ReExImport),
            cancellationToken: Ct
        );
        _clock.Advance(TimeSpan.FromDays(1));

        var refreshed = Widget("first", v1.EntityId);
        refreshed.Source = new EntitySource { System = SourceSystem.ReEx, ReExSchemaVersion = "2" };
        var result = await _sut.UpdateAsync(
            refreshed,
            1,
            Change(ChangeReason.ReExRefresh),
            cancellationToken: Ct
        );

        Assert.True(result.Changed);
    }

    [Fact]
    public async Task UpdateAsync_OnlyTheImportTimeInAListItemChanged_WritesNoVersion()
    {
        var first = Widget("first");
        first.Sources.Add(ReExSource("1", new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc)));
        var v1 = await _sut.InsertAsync(first, Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromDays(1));

        var refreshed = Widget("first", v1.EntityId);
        refreshed.Sources.Add(ReExSource("1", new DateTime(2027, 1, 2, 9, 0, 0, DateTimeKind.Utc)));
        var result = await _sut.UpdateAsync(refreshed, 1, Change(), cancellationToken: Ct);

        Assert.False(result.Changed);
    }

    [Fact]
    public async Task UpdateAsync_OtherFieldInAListItemChanged_WritesAVersion()
    {
        var at = new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var first = Widget("first");
        first.Sources.Add(ReExSource("1", at));
        var v1 = await _sut.InsertAsync(first, Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromDays(1));

        var refreshed = Widget("first", v1.EntityId);
        refreshed.Sources.Add(ReExSource("2", at));
        var result = await _sut.UpdateAsync(refreshed, 1, Change(), cancellationToken: Ct);

        Assert.True(result.Changed);
    }

    private static EntitySource ReExSource(string schemaVersion, DateTime importedAt) =>
        new()
        {
            System = SourceSystem.ReEx,
            ReExSchemaVersion = schemaVersion,
            ImportedAt = importedAt,
        };

    [Fact]
    public async Task UpdateAsync_TimeDiffersOnlyBelowAMillisecond_WritesNoVersion()
    {
        var at = new DateTime(2027, 1, 1, 9, 0, 0, 123, DateTimeKind.Utc);
        var first = Widget("first");
        first.SeenAt = at;
        var v1 = await _sut.InsertAsync(first, Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var again = Widget("first", v1.EntityId);
        again.SeenAt = at.AddTicks(4_000); // 0.4 ms later: the same instant once stored
        var result = await _sut.UpdateAsync(again, 1, Change(), cancellationToken: Ct);

        Assert.False(result.Changed);
    }

    [Fact]
    public async Task InsertAsync_BusinessKeyOfAClosedVersion_IsAllowedButNotOfACurrentOne()
    {
        var a = await _sut.InsertAsync(Widget("first"), Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _sut.UpdateAsync(Widget("second", a.EntityId), 1, Change(), cancellationToken: Ct);

        // Two versions of one entity carry code "A" (one closed), which the partial index allows.
        Assert.Equal(
            2,
            (await _sut.GetVersionsAsync(a.EntityId, cancellationToken: Ct)).Count(v =>
                v.Code == "A"
            )
        );

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() =>
            _sut.InsertAsync(Widget("other entity"), Change(), cancellationToken: Ct)
        );
        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError.Category);
    }

    [Fact]
    public async Task UpdateAsync_CallerTransactionAborted_RollsBackAndLeavesTheCallersObjectUnchanged()
    {
        var v1 = await _sut.InsertAsync(Widget("first"), Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var proposed = Widget("second", v1.EntityId);

        using var session = await _factory.GetClient().StartSessionAsync(cancellationToken: Ct);
        session.StartTransaction();
        await _sut.UpdateAsync(proposed, 1, Change(), session, Ct);
        await session.AbortTransactionAsync(Ct);

        var versions = await _sut.GetVersionsAsync(v1.EntityId, cancellationToken: Ct);
        Assert.True(Assert.Single(versions).IsCurrent);
        Assert.Equal(0, proposed.Version);
        Assert.Equal(ObjectId.Empty, proposed.Id);
        Assert.False(proposed.IsCurrent);
    }

    [Fact]
    public async Task UpdateAsync_SessionWithoutATransaction_ThrowsAndWritesNothing()
    {
        var v1 = await _sut.InsertAsync(Widget("first"), Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));

        using var session = await _factory.GetClient().StartSessionAsync(cancellationToken: Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.UpdateAsync(Widget("second", v1.EntityId), 1, Change(), session, Ct)
        );

        var versions = await _sut.GetVersionsAsync(v1.EntityId, cancellationToken: Ct);
        Assert.True(Assert.Single(versions).IsCurrent);
    }
}

public class WidgetVersion : VersionedEntityBase
{
    public string? Code { get; set; }

    public string? Name { get; set; }

    public DateTime? SeenAt { get; set; }

    public EntitySource? Source { get; set; }

    public List<EntitySource> Sources { get; set; } = [];
}
