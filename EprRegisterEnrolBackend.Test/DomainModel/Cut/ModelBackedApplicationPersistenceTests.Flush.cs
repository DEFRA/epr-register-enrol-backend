using EprRegisterEnrolBackend.DomainModel.Cut;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.Test.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace EprRegisterEnrolBackend.Test.DomainModel.Cut;

/// <summary>
/// The flush: in the transaction, every staged application is brought into the model by the same migrator and sync
/// dual-write uses (reading the staged copy through the model-backed persistence), and the staged copies are deleted,
/// so after the commit the model holds the change, legacy doesn't, and nothing is left staged.
/// </summary>
public sealed partial class ModelBackedApplicationPersistenceTests
{
    // As the container will wire it with LegacyWrites off: the migrator and the sync read through the model-backed
    // persistence.
    private CutFlush Flush()
    {
        var clock = new TestClock(new DateTime(2027, 2, 1, 9, 0, 0, DateTimeKind.Utc));
        return new CutFlush(
            new ApplicationGroupMigrator(_model, _stores, clock),
            new ApplicationModelSync(
                _model,
                _stores,
                clock,
                NullLogger<ApplicationModelSync>.Instance
            ),
            _staging
        );
    }

    [Fact]
    public async Task Flush_WritesTheStagedChangeToTheModelAndNotToLegacy()
    {
        var migrated = await MigratedAsync();
        var modelBefore = (await CurrentVersionAsync(migrated))!.Version;
        using (var session = await TransactionAsync())
        {
            var read = (await _model.GetByObjectIdAsync(migrated.Id!.Value, session, Ct))!;
            read.PaymentReference = "PAY-9";
            await _model.UpdateAsync(read, session);

            await Flush().FlushAsync(session, Ct);

            await session.CommitTransactionAsync(Ct);
        }

        var model = (await _model.GetByIdAsync(ReExOrganisationId, migrated.Id!.Value.ToString()))!;
        Assert.Equal("PAY-9", model.PaymentReference);
        Assert.Equal(modelBefore + 1, (await CurrentVersionAsync(migrated))!.Version);
        var legacy = (
            await _legacy.GetByIdAsync(migrated.OrganisationId, migrated.Id!.Value.ToString())
        )!;
        Assert.Equal(migrated.PaymentReference, legacy.PaymentReference);
        Assert.Equal(0, await StagedCountAsync());
    }

    // The sync's own logic runs on the staged copy: a site edited in the application reaches the model's projection.
    [Fact]
    public async Task Flush_ASiteEdit_ReachesTheModel()
    {
        var migrated = await MigratedAsync();
        using (var session = await TransactionAsync())
        {
            var read = (await _model.GetByObjectIdAsync(migrated.Id!.Value, session, Ct))!;
            read.OverseasSites!.Sites.Single(s => s.OrsId == "002").SiteName = "Renamed";
            await _model.UpdateAsync(read, session);

            await Flush().FlushAsync(session, Ct);

            await session.CommitTransactionAsync(Ct);
        }

        var model = (await _model.GetByIdAsync(ReExOrganisationId, migrated.Id!.Value.ToString()))!;
        Assert.Equal("Renamed", model.OverseasSites!.Sites.Single(s => s.OrsId == "002").SiteName);
    }

    [Fact]
    public async Task Flush_NothingStaged_ChangesNothing()
    {
        var migrated = await MigratedAsync();
        var modelBefore = (await CurrentVersionAsync(migrated))!.Version;
        using (var session = await TransactionAsync())
        {
            await Flush().FlushAsync(session, Ct);
            await session.CommitTransactionAsync(Ct);
        }

        Assert.Equal(modelBefore, (await CurrentVersionAsync(migrated))!.Version);
    }

    [Fact]
    public async Task Flush_ForgetsWhatItFlushed()
    {
        var migrated = await MigratedAsync();
        using var session = await TransactionAsync();
        var read = (await _model.GetByObjectIdAsync(migrated.Id!.Value, session, Ct))!;
        await _model.UpdateAsync(read, session);

        await Flush().FlushAsync(session, Ct);

        Assert.Empty(_staging.StagedIds(session));
        await session.AbortTransactionAsync(Ct);
    }
}
