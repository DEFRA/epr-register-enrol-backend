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

    [Fact]
    public async Task GetByObjectId_CurrentVersionWithCreatedAt_LogsNothing()
    {
        var legacy = await MigratedAsync();

        await ReadThroughModelAsync(legacy.Id!.Value);

        Assert.DoesNotContain(_logger.Entries, e => e.LogLevel >= LogLevel.Warning);
    }
}
