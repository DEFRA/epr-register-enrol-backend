using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Cut;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// RA-706: <c>DomainModel:LegacyWrites</c> off is the cut. The host refuses to start without its prerequisites, in
/// Development too (these hosts run as Development): a frozen legacy read is wrong in every environment.
/// </summary>
public sealed partial class DualWriteEndpointTests
{
    [Fact]
    public void LegacyWritesOffWithoutReadFromModel_InDevelopment_RefusesToStart()
    {
        var cut = new Dictionary<string, string?>(s_dualWriteOn)
        {
            ["DomainModel:DualWriteSeed"] = "true",
            ["DomainModel:LegacyWrites"] = "false",
        };

        var ex = Assert.Throws<InvalidOperationException>(() => Start(cut));

        Assert.StartsWith(
            "DomainModel:LegacyWrites off needs DomainModel:ReadFromModel",
            ex.Message
        );
    }

    // The createdAt backfill runs as a background service that logs and gives up on any failure, so a dependency the
    // host can't supply would only ever show as a log line: resolve it from a real host with the migration on.
    [Fact]
    public void MigrateOn_TheCreatedAtBackfillResolvesFromTheHost()
    {
        var (factory, client) = Start(
            new Dictionary<string, string?>(s_dualWriteOn)
            {
                ["DomainModel:MigrateEnabled"] = "true",
            }
        );
        using var _ = factory;
        using var __ = client;

        var backfill = factory.Services.GetService<ApplicationCreatedAtBackfill>();

        Assert.NotNull(backfill);
    }

    private static readonly Dictionary<string, string?> s_cut = new(s_dualWriteOn)
    {
        ["DomainModel:DualWriteSeed"] = "true",
        ["DomainModel:ReadFromModel"] = "true",
        ["DomainModel:LegacyWrites"] = "false",
    };

    [Fact]
    public void LegacyWritesOff_ApplicationsArePersistedThroughTheModel()
    {
        var (factory, client) = Start(s_cut);
        using var _ = factory;
        using var __ = client;

        var persistence =
            factory.Services.GetRequiredService<IAccreditationApplicationPersistence>();

        Assert.IsType<ModelBackedApplicationPersistence>(persistence);
    }

    // After the cut the persistence already projects from the model, so the reader serves what it returns as it is;
    // projecting it a second time, with "legacy" metadata that is itself the model's, would only cost a read.
    [Fact]
    public void LegacyWritesOff_ReadsServeThePersistenceAsItIs()
    {
        var (factory, client) = Start(s_cut);
        using var _ = factory;
        using var __ = client;

        Assert.IsType<PersistenceApplicationReader>(
            factory.Services.GetRequiredService<IApplicationReader>()
        );
    }

    // The migrator and sync the flush runs read and write applications through the same persistence the handlers use,
    // so a staged copy is what they see.
    [Fact]
    public void LegacyWritesOff_TheUnitOfWorkFlushesTheCut()
    {
        var (factory, client) = Start(s_cut);
        using var _ = factory;
        using var __ = client;

        Assert.NotNull(factory.Services.GetService<CutFlush>());
        Assert.IsType<DualWriteUnitOfWork>(
            factory.Services.GetRequiredService<IDualWriteUnitOfWork>()
        );
    }

    [Fact]
    public void LegacyWritesOn_ApplicationsArePersistedToLegacyAndThereIsNoCut()
    {
        var (factory, client) = Start(
            new Dictionary<string, string?>(s_cut) { ["DomainModel:LegacyWrites"] = "true" }
        );
        using var _ = factory;
        using var __ = client;

        Assert.IsType<AccreditationApplicationPersistence>(
            factory.Services.GetRequiredService<IAccreditationApplicationPersistence>()
        );
        Assert.Null(factory.Services.GetService<CutFlush>());
        Assert.Null(factory.Services.GetService<CutStagingPersistence>());
    }

    // Both read legacy as the source: after the cut legacy is frozen, so the migration would find nothing true to
    // migrate and reconciliation would report every write since the cut as drift.
    [Theory]
    [InlineData("MigrateEnabled")]
    [InlineData("ReconcileEnabled")]
    public void LegacyWritesOff_WithALegacyReadingService_RefusesToStart(string flag)
    {
        var settings = new Dictionary<string, string?>(s_cut) { [$"DomainModel:{flag}"] = "true" };

        var ex = Assert.Throws<InvalidOperationException>(() => Start(settings));

        Assert.StartsWith($"DomainModel:{flag} needs DomainModel:LegacyWrites", ex.Message);
    }
}
