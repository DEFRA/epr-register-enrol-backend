using System.Diagnostics.Metrics;
using EprRegisterEnrolBackend.DomainModel.Cut;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using EprRegisterEnrolBackend.Test.DomainModel.DualWrite;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.DomainModel.Cut;

/// <summary>
/// The conflicts the cut's flush returns are logged once the write has committed, as the dual-write unit of work logs
/// them, wherever the write ran.
/// </summary>
public sealed partial class ModelBackedApplicationPersistenceTests
{
    private static MigrationConflict ConflictOn(ObjectId applicationId) =>
        new()
        {
            Id = ObjectId.GenerateNewId(),
            Kind = MigrationConflictKind.OrsDetailsDiffer,
            ReExRegistrationId = ReExRegistrationId,
            OrsId = "001",
            SiteNumber = "002",
            WinnerApplicationId = applicationId,
            ApplicationIds = [applicationId],
        };

    // A migrator that reports one conflict for each application, the same one each time it is asked.
    private static RecordingGroupMigrator MigratorWithAConflict()
    {
        var conflicts = new Dictionary<ObjectId, MigrationConflict>();
        return new RecordingGroupMigrator
        {
            OnMigrate = (id, _) =>
            {
                if (!conflicts.TryGetValue(id, out var conflict))
                {
                    conflicts[id] = conflict = ConflictOn(id);
                }
                return new GroupMigrationResult(GroupMigrationOutcome.Migrated, 1, [conflict]);
            },
        };
    }

    [Fact]
    public async Task Update_WithoutASession_LogsTheConflictsTheFlushReturns()
    {
        var migrated = await MigratedAsync();
        var logger = new CapturingLogger<ModelBackedApplicationPersistence>();
        var model = new ModelBackedApplicationPersistence(
            _factory,
            _stores,
            new ApplicationReadModel(_stores),
            _staging,
            () => new CutFlush(MigratorWithAConflict(), new RecordingModelSync(), _staging),
            logger
        );
        var read = (
            await model.GetByIdAsync(migrated.OrganisationId, migrated.Id!.Value.ToString())
        )!;
        read.PaymentReference = "PAY-9";

        await model.UpdateAsync(read);

        var entry = Assert.Single(
            logger.Entries,
            e => e.Message.Contains(nameof(MigrationConflictKind.OrsDetailsDiffer))
        );
        Assert.Equal(LogLevel.Warning, entry.LogLevel);
        Assert.Contains(migrated.Id!.Value.ToString(), entry.Message);
    }

    [Fact]
    public async Task UnitOfWork_TheSameConflictFromTheMigrationAndTheFlush_IsLoggedOnce()
    {
        var migrated = await MigratedAsync();
        var migrator = MigratorWithAConflict();
        var sync = new RecordingModelSync();
        var logger = new CapturingLogger<DualWriteUnitOfWork>();
        var meters = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        var unitOfWork = new DualWriteUnitOfWork(
            _factory,
            migrator,
            sync,
            new DualWriteMetrics(meters),
            logger,
            new CutFlush(migrator, sync, _staging)
        );

        await unitOfWork.RunAsync(
            "Test",
            migrated.Id!.Value.ToString(),
            async (session, _) =>
            {
                await SetPaymentReferenceAsync(migrated, "PAY-9", session);
                return Results.Ok();
            },
            Ct
        );

        Assert.Single(
            logger.Entries,
            e => e.Message.Contains(nameof(MigrationConflictKind.OrsDetailsDiffer))
        );
    }
}
