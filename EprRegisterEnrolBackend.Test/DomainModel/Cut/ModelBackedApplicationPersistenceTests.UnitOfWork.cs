using System.Diagnostics.Metrics;
using EprRegisterEnrolBackend.DomainModel.Cut;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Test.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EprRegisterEnrolBackend.Test.DomainModel.Cut;

/// <summary>
/// The dual-write unit of work with the cut's flush: each attempt starts with nothing staged, the work's writes are
/// staged, and before the commit every staged application (not only the one the unit is for) is flushed to the model.
/// </summary>
public sealed partial class ModelBackedApplicationPersistenceTests
{
    private DualWriteUnitOfWork CutUnitOfWork()
    {
        var clock = new TestClock(new DateTime(2027, 2, 1, 9, 0, 0, DateTimeKind.Utc));
        var migrator = new ApplicationGroupMigrator(_model, _stores, clock);
        var sync = new ApplicationModelSync(
            _model,
            _stores,
            clock,
            NullLogger<ApplicationModelSync>.Instance
        );
        var meters = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        return new DualWriteUnitOfWork(
            _factory,
            migrator,
            sync,
            new DualWriteMetrics(meters),
            NullLogger<DualWriteUnitOfWork>.Instance,
            new CutFlush(migrator, sync, _staging)
        );
    }

    private async Task SetPaymentReferenceAsync(
        AccreditationApplicationModel application,
        string reference,
        MongoDB.Driver.IClientSessionHandle? session
    )
    {
        var read = (await _model.GetByObjectIdAsync(application.Id!.Value, session!, Ct))!;
        read.PaymentReference = reference;
        Assert.NotNull(await _model.UpdateAsync(read, session));
    }

    private async Task<string?> ModelPaymentReferenceAsync(
        AccreditationApplicationModel application
    ) =>
        (
            await _model.GetByIdAsync(application.OrganisationId, application.Id!.Value.ToString())
        )!.PaymentReference;

    [Fact]
    public async Task UnitOfWork_WithTheCutFlush_WritesTheModelAndLeavesLegacyAlone()
    {
        var migrated = await MigratedAsync();

        var result = await CutUnitOfWork()
            .RunAsync(
                "Test",
                migrated.Id!.Value.ToString(),
                async (session, _) =>
                {
                    await SetPaymentReferenceAsync(migrated, "PAY-9", session);
                    return Results.Ok();
                },
                Ct
            );

        Assert.IsType<Ok>(result);
        Assert.Equal("PAY-9", await ModelPaymentReferenceAsync(migrated));
        var legacy = (
            await _legacy.GetByIdAsync(migrated.OrganisationId, migrated.Id!.Value.ToString())
        )!;
        Assert.Equal(migrated.PaymentReference, legacy.PaymentReference);
        Assert.Equal(0, await StagedCountAsync());
    }

    // The registration number is copied to the other in-flight applications in the same unit; every application the
    // unit wrote must reach the model, not only the one it was run for.
    [Fact]
    public async Task UnitOfWork_WritingTwoApplications_FlushesBoth()
    {
        var first = await MigratedAsync();
        var second = await MigratedAsync(NewApplication(OtherOrganisationId));

        await CutUnitOfWork()
            .RunAsync(
                "Test",
                first.Id!.Value.ToString(),
                async (session, _) =>
                {
                    await SetPaymentReferenceAsync(first, "PAY-1", session);
                    await SetPaymentReferenceAsync(second, "PAY-2", session);
                    return Results.Ok();
                },
                Ct
            );

        Assert.Equal("PAY-1", await ModelPaymentReferenceAsync(first));
        Assert.Equal("PAY-2", await ModelPaymentReferenceAsync(second));
    }

    [Fact]
    public async Task UnitOfWork_WorkFails_LeavesTheModelAndStagingUnchanged()
    {
        var migrated = await MigratedAsync();
        var modelBefore = (await CurrentVersionAsync(migrated))!.Version;

        var result = await CutUnitOfWork()
            .RunAsync(
                "Test",
                migrated.Id!.Value.ToString(),
                async (session, _) =>
                {
                    await SetPaymentReferenceAsync(migrated, "PAY-9", session);
                    throw new InvalidOperationException("the work failed after its write");
                },
                Ct
            );

        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            Assert.IsType<ProblemHttpResult>(result).StatusCode
        );
        Assert.Equal(modelBefore, (await CurrentVersionAsync(migrated))!.Version);
        Assert.Equal(migrated.PaymentReference, await ModelPaymentReferenceAsync(migrated));
        Assert.Equal(0, await StagedCountAsync());
    }

    // A retried attempt reuses the session; it must not inherit what the attempt before it staged and rolled back.
    [Fact]
    public async Task CutFlush_Begin_ClearsWhatTheSessionStaged()
    {
        var migrated = await MigratedAsync();
        using var session = await TransactionAsync();
        var read = (await _model.GetByObjectIdAsync(migrated.Id!.Value, session, Ct))!;
        await _model.UpdateAsync(read, session);

        Flush().Begin(session);

        Assert.Empty(_staging.StagedIds(session));
        await session.AbortTransactionAsync(Ct);
    }
}
