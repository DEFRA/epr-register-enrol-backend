using EprRegisterEnrolBackend.DomainModel.Persistence;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Cut;

/// <summary>
/// RA-706 decision 5: the createdAt backfill fills every version written before the model held it, and cut-readiness
/// check A lists any it missed. An application read without one still needs a time (the legacy shape requires it), so
/// it gets when its current version was written, and says so in the log rather than quietly reordering the list.
/// </summary>
public sealed partial class ModelBackedApplicationPersistenceTests
{
    [Fact]
    public async Task GetByObjectId_CurrentVersionWithoutCreatedAt_UsesValidFromAndLogsAWarning()
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
        var current = (await CurrentVersionAsync(legacy))!;

        var model = (await ReadThroughModelAsync(legacy.Id!.Value))!;

        Assert.Equal(current.ValidFrom, model.CreatedAt);
        Assert.Contains(
            _logger.Entries,
            e =>
                e.LogLevel == LogLevel.Warning
                && e.Message.Contains(legacy.Id!.Value.ToString())
                && e.Message.Contains("createdAt")
        );
    }

    // A read can serve the fallback, but a write stages the projection and the sync would store whatever time it
    // carries: a time made up at read time must never reach the model.
    [Fact]
    public async Task Update_CurrentVersionWithoutCreatedAt_RefusesAndStoresNoInventedTime()
    {
        var legacy = await MigratedAsync();
        var versions = _factory
            .GetClient()
            .GetDatabase(_databaseName)
            .GetCollection<BsonDocument>(DomainModelCollections.ApplicationVersions);
        await versions.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Eq("entityId", legacy.Id!.Value),
            Builders<BsonDocument>.Update.Unset("createdAt"),
            cancellationToken: Ct
        );
        var read = (await ReadThroughModelAsync(legacy.Id!.Value))!;
        read.PaymentReference = "PAY-9";

        await Assert.ThrowsAsync<InvalidOperationException>(() => _model.UpdateAsync(read));

        var stored = await versions
            .Find(
                Builders<BsonDocument>.Filter.Eq("entityId", legacy.Id!.Value)
                    & Builders<BsonDocument>.Filter.Eq("isCurrent", true)
            )
            .SingleAsync(Ct);
        Assert.False(stored.Contains("createdAt") && !stored["createdAt"].IsBsonNull);
        Assert.Equal(0, await StagedCountAsync());
    }

    [Fact]
    public async Task GetByObjectId_CurrentVersionWithCreatedAt_LogsNothing()
    {
        var legacy = await MigratedAsync();

        await ReadThroughModelAsync(legacy.Id!.Value);

        Assert.DoesNotContain(_logger.Entries, e => e.LogLevel >= LogLevel.Warning);
    }
}
