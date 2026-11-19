using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Cut;

/// <summary>
/// Writes inside a transaction: the legacy persistence's own logic runs against a staged copy of the application
/// (its version check, guard filters and update operators), reads in the same transaction see the result, and neither
/// the model nor legacy changes until the staged copy is flushed (the next step). Nothing staged outlives the
/// transaction.
/// </summary>
public sealed partial class ModelBackedApplicationPersistenceTests
{
    private async Task<IClientSessionHandle> TransactionAsync()
    {
        var session = await _factory.GetClient().StartSessionAsync(cancellationToken: Ct);
        session.StartTransaction();
        return session;
    }

    private Task<long> StagedCountAsync() =>
        _factory
            .GetClient()
            .GetDatabase(_databaseName)
            .GetCollection<BsonDocument>(DomainModelCollections.CutStagingApplications)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

    [Fact]
    public async Task Update_InATransaction_IsReadBackInThatTransactionWithTheNextVersion()
    {
        var migrated = await MigratedAsync();
        using var session = await TransactionAsync();
        var read = (await _model.GetByObjectIdAsync(migrated.Id!.Value, session, Ct))!;
        var readVersion = read.Version;
        read.PaymentReference = "PAY-9";

        var updated = await _model.UpdateAsync(read, session);

        Assert.Equal(readVersion + 1, updated!.Version);
        var again = (await _model.GetByObjectIdAsync(migrated.Id!.Value, session, Ct))!;
        Assert.Equal("PAY-9", again.PaymentReference);
        Assert.Equal(readVersion + 1, again.Version);
        await session.AbortTransactionAsync(Ct);
    }

    [Fact]
    public async Task Update_InATransaction_ChangesNeitherTheModelNorLegacyBeforeTheFlush()
    {
        var migrated = await MigratedAsync();
        var modelBefore = (await CurrentVersionAsync(migrated))!.Version;
        using var session = await TransactionAsync();
        var read = (await _model.GetByObjectIdAsync(migrated.Id!.Value, session, Ct))!;
        read.PaymentReference = "PAY-9";

        await _model.UpdateAsync(read, session);

        var modelInSession = await _stores.Applications.Versions.GetCurrentAsync(
            migrated.Id!.Value,
            session,
            Ct
        );
        Assert.Equal(modelBefore, modelInSession!.Version);
        await session.CommitTransactionAsync(Ct);
        var legacy = (
            await _legacy.GetByIdAsync(migrated.OrganisationId, migrated.Id!.Value.ToString())
        )!;
        Assert.Equal(migrated.PaymentReference, legacy.PaymentReference);
        Assert.Equal(migrated.Version, legacy.Version);
    }

    // The legacy optimistic-concurrency rule (RA-516): a copy read before another write is refused.
    [Fact]
    public async Task Update_StaleCopy_IsRefused()
    {
        var migrated = await MigratedAsync();
        using var session = await TransactionAsync();
        var first = (await _model.GetByObjectIdAsync(migrated.Id!.Value, session, Ct))!;
        var stale = (await _model.GetByObjectIdAsync(migrated.Id!.Value, session, Ct))!;
        first.PaymentReference = "PAY-1";
        await _model.UpdateAsync(first, session);
        stale.PaymentReference = "PAY-2";

        Assert.Null(await _model.UpdateAsync(stale, session));
        await session.AbortTransactionAsync(Ct);
    }

    [Theory]
    [InlineData(ApplicationStatus.Started, "PAY-7")]
    [InlineData(ApplicationStatus.Submitted, null)]
    public async Task UpdateFields_AppliesTheUpdateOnlyWhereTheGuardMatches(
        ApplicationStatus guardStatus,
        string? expected
    )
    {
        var migrated = await MigratedAsync();
        using var session = await TransactionAsync();

        var updated = await _model.UpdateFieldsAsync(
            migrated.Id!.Value,
            Builders<AccreditationApplicationModel>.Filter.Eq(
                a => a.ApplicationStatus,
                guardStatus
            ),
            Builders<AccreditationApplicationModel>.Update.Set(a => a.PaymentReference, "PAY-7"),
            session,
            Ct
        );

        Assert.Equal(expected, updated?.PaymentReference);
        var read = (await _model.GetByObjectIdAsync(migrated.Id!.Value, session, Ct))!;
        Assert.Equal(expected ?? migrated.PaymentReference, read.PaymentReference);
        await session.AbortTransactionAsync(Ct);
    }

    [Fact]
    public async Task Lists_InATransaction_ShowTheStagedCopy()
    {
        var migrated = await MigratedAsync();
        using var session = await TransactionAsync();
        var read = (await _model.GetByObjectIdAsync(migrated.Id!.Value, session, Ct))!;
        read.PaymentReference = "PAY-9";
        await _model.UpdateAsync(read, session);

        var group = await _model.GetRegistrationGroupAsync(
            ReExOrganisationId,
            ReExRegistrationId,
            50,
            session,
            Ct
        );

        Assert.Equal("PAY-9", Assert.Single(group).PaymentReference);
        await session.AbortTransactionAsync(Ct);
    }

    [Fact]
    public async Task StagedIds_ListWhatTheTransactionWrote()
    {
        var migrated = await MigratedAsync();
        using var session = await TransactionAsync();
        var read = (await _model.GetByObjectIdAsync(migrated.Id!.Value, session, Ct))!;
        Assert.Empty(_staging.StagedIds(session));

        await _model.UpdateAsync(read, session);

        Assert.Equal([migrated.Id!.Value], _staging.StagedIds(session));
        await session.AbortTransactionAsync(Ct);
    }

    [Fact]
    public async Task AbortedTransaction_LeavesNothingStaged()
    {
        var migrated = await MigratedAsync();
        using (var session = await TransactionAsync())
        {
            var read = (await _model.GetByObjectIdAsync(migrated.Id!.Value, session, Ct))!;
            await _model.UpdateAsync(read, session);
            await session.AbortTransactionAsync(Ct);
        }

        Assert.Equal(0, await StagedCountAsync());
    }
}
