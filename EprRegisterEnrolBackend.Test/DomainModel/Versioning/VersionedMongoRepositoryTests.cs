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
                Builders<WidgetVersion>.IndexKeys.Ascending("code")
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

    [Fact]
    public async Task Insert_writes_version_one_as_the_current_version()
    {
        var created = await _sut.InsertAsync(
            new WidgetVersion { Code = "A", Name = "first" },
            Change(), cancellationToken: Ct
        );

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
    public async Task Update_closes_the_old_version_and_inserts_the_next_in_one_step()
    {
        var v1 = await _sut.InsertAsync(new WidgetVersion { Code = "A", Name = "first" }, Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromHours(1));

        var result = await _sut.UpdateAsync(
            new WidgetVersion
            {
                EntityId = v1.EntityId,
                Code = "A",
                Name = "second",
            },
            expectedVersion: 1,
            Change(ChangeReason.Suspended), cancellationToken: Ct
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
    public async Task Update_with_a_stale_expected_version_is_a_conflict_and_writes_nothing()
    {
        var v1 = await _sut.InsertAsync(new WidgetVersion { Code = "A", Name = "first" }, Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _sut.UpdateAsync(
            new WidgetVersion
            {
                EntityId = v1.EntityId,
                Code = "A",
                Name = "second",
            },
            1,
            Change(), cancellationToken: Ct
        );

        await Assert.ThrowsAsync<VersionConflictException>(() =>
            _sut.UpdateAsync(
                new WidgetVersion
                {
                    EntityId = v1.EntityId,
                    Code = "A",
                    Name = "third",
                },
                1,
                Change(), cancellationToken: Ct
            )
        );

        Assert.Equal(2, (await _sut.GetVersionsAsync(v1.EntityId, cancellationToken: Ct)).Count);
    }

    [Fact]
    public async Task Two_writers_racing_from_the_same_version_produce_one_winner_and_one_conflict()
    {
        var v1 = await _sut.InsertAsync(new WidgetVersion { Code = "A", Name = "first" }, Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var attempts = Enumerable
            .Range(0, 2)
            .Select(i =>
                Task.Run(async () =>
                {
                    try
                    {
                        await _sut.UpdateAsync(
                            new WidgetVersion
                            {
                                EntityId = v1.EntityId,
                                Code = "A",
                                Name = $"writer {i}",
                            },
                            1,
                            Change(), cancellationToken: Ct
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
    public async Task Writes_in_the_same_millisecond_still_get_distinct_ordered_periods()
    {
        var v1 = await _sut.InsertAsync(new WidgetVersion { Code = "A", Name = "first" }, Change(), cancellationToken: Ct);

        // The clock does not move, so both updates land in the insert's millisecond.
        await _sut.UpdateAsync(
            new WidgetVersion
            {
                EntityId = v1.EntityId,
                Code = "A",
                Name = "second",
            },
            1,
            Change(), cancellationToken: Ct
        );
        await _sut.UpdateAsync(
            new WidgetVersion
            {
                EntityId = v1.EntityId,
                Code = "A",
                Name = "third",
            },
            2,
            Change(), cancellationToken: Ct
        );

        var versions = await _sut.GetVersionsAsync(v1.EntityId, cancellationToken: Ct);
        Assert.Equal(3, versions.Count);
        Assert.All(versions.Take(2), v => Assert.True(v.ValidFrom < v.ValidTo));
        Assert.True(versions[0].ValidFrom < versions[1].ValidFrom);
        Assert.True(versions[1].ValidFrom < versions[2].ValidFrom);
        Assert.Equal(versions[2].ValidFrom, versions[1].ValidTo);
    }

    [Fact]
    public async Task As_of_reads_return_the_version_that_was_current_then()
    {
        var t0 = _clock.UtcNow.UtcDateTime;
        var v1 = await _sut.InsertAsync(new WidgetVersion { Code = "A", Name = "first" }, Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromHours(2));
        await _sut.UpdateAsync(
            new WidgetVersion
            {
                EntityId = v1.EntityId,
                Code = "A",
                Name = "second",
            },
            1,
            Change(), cancellationToken: Ct
        );

        Assert.Equal("first", (await _sut.GetAsOfAsync(v1.EntityId, t0.AddHours(1), cancellationToken: Ct))!.Name);
        Assert.Equal("second", (await _sut.GetAsOfAsync(v1.EntityId, t0.AddHours(3), cancellationToken: Ct))!.Name);
        Assert.Null(await _sut.GetAsOfAsync(v1.EntityId, t0.AddMinutes(-1), cancellationToken: Ct));
        // Periods are half-open: at the instant of the change, the new version applies.
        Assert.Equal("second", (await _sut.GetAsOfAsync(v1.EntityId, t0.AddHours(2), cancellationToken: Ct))!.Name);
    }

    [Fact]
    public async Task An_update_that_changes_nothing_writes_no_version()
    {
        var v1 = await _sut.InsertAsync(new WidgetVersion { Code = "A", Name = "first" }, Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await _sut.UpdateAsync(
            new WidgetVersion
            {
                EntityId = v1.EntityId,
                Code = "A",
                Name = "first",
            },
            1,
            Change(ChangeReason.ReExRefresh), cancellationToken: Ct
        );

        Assert.False(result.Changed);
        Assert.Equal(1, result.Entity.Version);
        Assert.Single(await _sut.GetVersionsAsync(v1.EntityId, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_business_key_may_repeat_across_versions_but_not_across_current_versions()
    {
        var a = await _sut.InsertAsync(new WidgetVersion { Code = "A", Name = "first" }, Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _sut.UpdateAsync(
            new WidgetVersion
            {
                EntityId = a.EntityId,
                Code = "A",
                Name = "second",
            },
            1,
            Change(), cancellationToken: Ct
        );

        // Two versions of one entity carry code "A" (one closed), which the partial index allows.
        Assert.Equal(2, (await _sut.GetVersionsAsync(a.EntityId, cancellationToken: Ct)).Count(v => v.Code == "A"));

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() =>
            _sut.InsertAsync(new WidgetVersion { Code = "A", Name = "other entity" }, Change(), cancellationToken: Ct)
        );
        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError.Category);
    }

    [Fact]
    public async Task Participating_in_a_caller_transaction_rolls_back_with_it()
    {
        var v1 = await _sut.InsertAsync(new WidgetVersion { Code = "A", Name = "first" }, Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));

        using var session = await _factory.GetClient().StartSessionAsync(cancellationToken: Ct);
        session.StartTransaction();
        await _sut.UpdateAsync(
            new WidgetVersion
            {
                EntityId = v1.EntityId,
                Code = "A",
                Name = "second",
            },
            1,
            Change(),
            session,
            Ct
        );
        await session.AbortTransactionAsync(Ct);

        var versions = await _sut.GetVersionsAsync(v1.EntityId, cancellationToken: Ct);
        Assert.Single(versions);
        Assert.True(versions[0].IsCurrent);
    }

    [Fact]
    public async Task FindCurrent_returns_only_current_versions_matching_the_filter()
    {
        var a = await _sut.InsertAsync(new WidgetVersion { Code = "A", Name = "shared" }, Change(), cancellationToken: Ct);
        await _sut.InsertAsync(new WidgetVersion { Code = "B", Name = "shared" }, Change(), cancellationToken: Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _sut.UpdateAsync(
            new WidgetVersion
            {
                EntityId = a.EntityId,
                Code = "A",
                Name = "renamed",
            },
            1,
            Change(), cancellationToken: Ct
        );

        var found = await _sut.FindCurrentAsync(
            Builders<WidgetVersion>.Filter.Eq(w => w.Name, "shared"),
            cancellationToken: Ct
        );

        Assert.Equal(["B"], found.Select(w => w.Code));
    }
}

public class WidgetVersion : VersionedEntityBase
{
    public string? Code { get; set; }

    public string? Name { get; set; }
}
