using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-788: bound from Migrations:Ra788DuplicateApplicationDeletion. Every setting defaults off, so
// the migration is a no-op in every environment until deliberately switched on.
public sealed class DuplicateApplicationDeletionOptions
{
    public const string SectionName = "Migrations:Ra788DuplicateApplicationDeletion";

    // Off: the migration does nothing at all (not even a read).
    public bool Enabled { get; set; }

    // Off (with Enabled on): dry run - logs what it would delete, writes nothing.
    public bool Apply { get; set; }

    // Who spot-checked the record against the dry-run output. Required before Apply takes effect;
    // logged with the deletion so the authorisation outlives the env var.
    public string? SpotCheckConfirmedBy { get; set; }
}

// RA-788 (INC2560371): two live 2027 Plastic applications exist for the same registration, because
// the seed endpoint's read-then-create is not atomic. This one-off deletes the duplicate, a Saved
// draft, and keeps the Updated one:
//
//   delete  6aa158537800ee4eb482371b  orgId null    registration 68da9750c020f0e3dff14278  Saved
//   keep    6aa158537800ee4eb482371c  orgId 500131  registration 68da9750c020f0e3dff14278  Updated
//
// Scoped to exactly these two ids; there is deliberately no query that could widen it. Only the
// legacy accreditationApplications collection is touched - the domain model is out of scope. Records
// linked to the deleted one are left in place and not checked. A Saved application has never been
// submitted to case management, so there is nothing there to tidy. There is no backup copy: a
// restore comes from the CDP backup.
//
// Checks (read only - any failure aborts with nothing written):
//   1. the target exists; if it is already gone and the keeper is still live, the run is a no-op;
//   2. the target is the 2027 Plastic application for the registration, still Saved, and holds
//      orgId null or 500131 (a GET backfills orgId, so either is expected);
//   3. the keeper exists, is still Updated, and shares the target's registration, year, material
//      and ReEx organisation id;
//   4. the group (ReEx organisation, registration, material, year) holds exactly 2 live
//      (not Withdrawn) applications - the same grouping the seed endpoint uses;
//   5. the target's Version is recorded.
//
// Delete, in one transaction: DeleteOne on the target guarded on that Version, Saved and every
// identity value, which must delete exactly 1; then the keeper must still be live and the group
// must hold exactly 1 live application. Any failure rolls the transaction back, so nothing is
// deleted. Each step is guarded, so a retry or a second instance does nothing further.
//
// Logging: every line starts RA-788 and states the mode. Only ids, statuses and counts are logged,
// and an exception is logged by its type only, as its message could carry personal data.
//
// A BackgroundService, so a failure here logs rather than crash-looping the host. The Mongo client
// is resolved only once the migration is enabled.
//
// Temporary: removed, with its tests, Program.cs registration, appsettings section, Serilog
// override and cdp-app-config variables, once production is confirmed fixed.
public class DuplicateApplicationDeletionMigration(
    IServiceProvider serviceProvider,
    IOptions<DuplicateApplicationDeletionOptions> options,
    ILogger<DuplicateApplicationDeletionMigration> logger
) : BackgroundService
{
    internal static readonly ObjectId TargetId = ObjectId.Parse("6aa158537800ee4eb482371b");
    internal static readonly ObjectId KeeperId = ObjectId.Parse("6aa158537800ee4eb482371c");
    internal const string RegistrationId = "68da9750c020f0e3dff14278";
    internal const int OrgId = 500131;
    internal const int AccreditationYear = 2027;
    internal const MaterialType Material = MaterialType.Plastic;

    private const string DryRunMode = "DRY RUN (nothing will be written)";
    private const string ApplyMode = "APPLY";

    internal enum RunResult
    {
        Disabled,
        DryRun,
        AlreadyDone,
        Applied,
        Aborted,
        RolledBack,
    }

    // Test seams: run between the checks and the transaction, and inside the transaction straight
    // after the delete, so a concurrent edit and a fault part-way can be simulated.
    internal Func<CancellationToken, Task>? BeforeDeleteForTest { get; set; }
    internal Func<CancellationToken, Task>? AfterDeleteForTest { get; set; }

    private static FilterDefinitionBuilder<AccreditationApplicationModel> Filter =>
        Builders<AccreditationApplicationModel>.Filter;


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is never held up by anything below.
        await Task.Yield();
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // The type only: a message could carry personal data. Every check runs before the
            // transaction and the delete is guarded, so the next run starts again safely.
            logger.LogError(
                "RA-788 duplicate application deletion failed: error={Error}",
                ex.GetType().Name
            );
        }
    }

    // Extracted so tests can await the run deterministically - BackgroundService's StartAsync only
    // kicks ExecuteAsync off as a background task.
    internal async Task<RunResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return RunResult.Disabled;

        // Null for a dry run; otherwise who authorised the apply.
        var confirmedBy = AuthorisedReviewer(options.Value);
        var mode = confirmedBy is null ? DryRunMode : ApplyMode;
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-788 {Mode:l}: duplicate application deletion starting. target={TargetId} "
                    + "keeper={KeeperId} registration={RegistrationId}",
                mode,
                TargetId,
                KeeperId,
                RegistrationId
            );

        var factory = serviceProvider.GetRequiredService<IMongoDbClientFactory>();
        var collection = factory.GetCollection<AccreditationApplicationModel>(
            DomainModelCollections.LegacyApplications
        );

        var check = await CheckAsync(collection, mode, cancellationToken);
        if (check.Result is { } early)
            return early;
        var (target, version) = (check.Target!, check.Version);

        if (confirmedBy is null)
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "RA-788 {Mode:l}: would delete application {TargetId} (status={Status} "
                        + "version={Version} orgId={TargetOrgId}) and keep {KeeperId}",
                    mode,
                    TargetId,
                    target.ApplicationStatus,
                    version,
                    OrgIdText(target.OrgId),
                    KeeperId
                );
            return RunResult.DryRun;
        }

        return await DeleteAsync(factory, collection, target, version, confirmedBy, cancellationToken);
    }

    // Apply takes effect only with a named reviewer; without one the run stays a dry run.
    private string? AuthorisedReviewer(DuplicateApplicationDeletionOptions settings)
    {
        if (!settings.Apply)
            return null;
        if (!string.IsNullOrWhiteSpace(settings.SpotCheckConfirmedBy))
            return settings.SpotCheckConfirmedBy;

        logger.LogError(
            "RA-788 {Mode:l}: Apply is set but SpotCheckConfirmedBy is not, so running as a dry run. "
                + "Set it to whoever spot-checked the record.",
            DryRunMode
        );
        return null;
    }

    private sealed record CheckOutcome(
        RunResult? Result,
        AccreditationApplicationModel? Target,
        long Version
    );

    private async Task<CheckOutcome> CheckAsync(
        IMongoCollection<AccreditationApplicationModel> collection,
        string mode,
        CancellationToken cancellationToken
    )
    {
        var target = await FindByIdAsync(collection, TargetId, cancellationToken);
        var keeper = await FindByIdAsync(collection, KeeperId, cancellationToken);

        // 1. Already deleted: a no-op, provided the keeper is still there and live.
        if (target is null)
        {
            if (keeper is not null && keeper.ApplicationStatus != ApplicationStatus.Withdrawn)
            {
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                        "RA-788 {Mode:l}: application {TargetId} already deleted and keeper "
                            + "{KeeperId} is live (status={KeeperStatus}); skipped",
                        mode,
                        TargetId,
                        KeeperId,
                        keeper.ApplicationStatus
                    );
                return new CheckOutcome(RunResult.AlreadyDone, null, 0);
            }
            return Abort(
                mode,
                Invariant(
                    $"target {TargetId} not found and keeper {KeeperId} is ",
                    keeper is null ? (FormattableString)$"missing" : $"status={keeper.ApplicationStatus}"
                )
            );
        }

        // 2. The target is the one we were asked about.
        if (!IsTarget(target))
            return Abort(
                mode,
                Invariant(
                    $"target {TargetId} is year={target.Year} material={target.MaterialType} ",
                    $"registration={target.RegistrationId ?? "null"} ",
                    $"orgId={OrgIdText(target.OrgId)} status={target.ApplicationStatus}; ",
                    $"expected year={AccreditationYear} material={Material} ",
                    $"registration={RegistrationId} orgId=null or {OrgId} status=Saved"
                )
            );

        // 3. The keeper exists, is still Updated and is in the same group.
        if (keeper is null)
            return Abort(mode, Invariant($"keeper {KeeperId} not found"));
        if (!IsKeeper(keeper, target))
            return Abort(
                mode,
                Invariant(
                    $"keeper {KeeperId} is year={keeper.Year} material={keeper.MaterialType} ",
                    $"registration={keeper.RegistrationId ?? "null"} ",
                    $"orgId={OrgIdText(keeper.OrgId)} status={keeper.ApplicationStatus} ",
                    $"sameReExOrganisation={keeper.OrganisationId == target.OrganisationId}; ",
                    $"expected the target's group, orgId={OrgId} and status=Updated"
                )
            );

        // 4. Exactly the two live applications in the group.
        var live = await collection.CountDocumentsAsync(
            LiveInGroup(target.OrganisationId),
            cancellationToken: cancellationToken
        );
        if (live != 2)
            return Abort(
                mode,
                Invariant($"the group holds {live} live applications; expected 2")
            );

        // 5. The Version the delete is guarded on.
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-788 {Mode:l}: checks passed. target={TargetId} status={TargetStatus} "
                    + "version={Version} keeper={KeeperId} status={KeeperStatus} live={LiveCount}",
                mode,
                TargetId,
                target.ApplicationStatus,
                target.Version,
                KeeperId,
                keeper.ApplicationStatus,
                live
            );
        return new CheckOutcome(null, target, target.Version);
    }

    // The reason holds only ids, statuses, years, materials and counts (see the class comment).
    private CheckOutcome Abort(string mode, string reason)
    {
        logger.LogError(
            "RA-788 {Mode:l}: aborted, nothing has been written: {Reason:l}",
            mode,
            reason
        );
        return new CheckOutcome(RunResult.Aborted, null, 0);
    }

    private static string Invariant(params FormattableString[] parts) =>
        string.Concat(parts.Select(p => p.ToString(CultureInfo.InvariantCulture)));

    private async Task<RunResult> DeleteAsync(
        IMongoDbClientFactory factory,
        IMongoCollection<AccreditationApplicationModel> collection,
        AccreditationApplicationModel target,
        long version,
        string confirmedBy,
        CancellationToken cancellationToken
    )
    {
        if (BeforeDeleteForTest is { } before)
            await before(cancellationToken);

        // Set when an in-transaction check fails; throwing then aborts the transaction, and the
        // reason (ids and counts only) is what is logged rather than an exception's message.
        string? rollbackReason = null;
        void FailCheck(string reason)
        {
            rollbackReason = reason;
            throw new InvalidOperationException(reason);
        }

        try
        {
            using var session = await factory
                .GetClient()
                .StartSessionAsync(cancellationToken: cancellationToken);
            await session.WithTransactionAsync(
                async (s, token) =>
                {
                    // The driver re-runs this on a transient error, so start each attempt clean.
                    rollbackReason = null;

                    // 1. Exactly the record checked, unchanged since.
                    var deleted = await collection.DeleteOneAsync(
                        s,
                        GuardedTarget(target.OrganisationId, version),
                        cancellationToken: token
                    );
                    if (deleted.DeletedCount != 1)
                        FailCheck(
                            Invariant(
                                $"deleted {deleted.DeletedCount} records; the target changed after the checks"
                            )
                        );

                    if (AfterDeleteForTest is { } after)
                        await after(token);

                    // 2. The keeper is still live.
                    var keeperLive = await collection.CountDocumentsAsync(
                        s,
                        Filter.And(
                            Filter.Eq(a => a.Id, KeeperId),
                            Filter.Ne(a => a.ApplicationStatus, ApplicationStatus.Withdrawn)
                        ),
                        cancellationToken: token
                    );
                    if (keeperLive != 1)
                        FailCheck("the keeper is no longer live");

                    // 3. Exactly one live application left in the group.
                    var live = await collection.CountDocumentsAsync(
                        s,
                        LiveInGroup(target.OrganisationId),
                        cancellationToken: token
                    );
                    if (live != 1)
                        FailCheck(
                            Invariant($"the group would hold {live} live applications; expected 1")
                        );
                    return true;
                },
                DomainModelTransactions.Options,
                cancellationToken
            );
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "RA-788 {Mode:l}: rolled back, nothing has been deleted. target={TargetId} "
                    + "reason={Reason:l}",
                ApplyMode,
                TargetId,
                rollbackReason ?? ex.GetType().Name
            );
            return RunResult.RolledBack;
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-788 APPLIED: deleted application {TargetId} (version={Version}); kept "
                    + "{KeeperId}. Spot check confirmed by {ConfirmedBy}",
                TargetId,
                version,
                KeeperId,
                confirmedBy
            );
        return RunResult.Applied;
    }

    private static async Task<AccreditationApplicationModel?> FindByIdAsync(
        IMongoCollection<AccreditationApplicationModel> collection,
        ObjectId id,
        CancellationToken cancellationToken
    ) =>
        await collection
            .Find(Filter.Eq(a => a.Id, id))
            .FirstOrDefaultAsync(cancellationToken);

    private static bool IsTarget(AccreditationApplicationModel target) =>
        target.RegistrationId == RegistrationId
        && target.Year == AccreditationYear
        && target.MaterialType == Material
        && target.OrgId is null or OrgId
        && target.ApplicationStatus == ApplicationStatus.Saved;

    private static bool IsKeeper(
        AccreditationApplicationModel keeper,
        AccreditationApplicationModel target
    ) =>
        keeper.RegistrationId == RegistrationId
        && keeper.Year == AccreditationYear
        && keeper.MaterialType == Material
        && keeper.OrganisationId == target.OrganisationId
        && keeper.OrgId == OrgId
        && keeper.ApplicationStatus == ApplicationStatus.Updated;

    private static FilterDefinition<AccreditationApplicationModel> LiveInGroup(
        string organisationId
    ) =>
        Filter.And(
            Filter.Eq(a => a.OrganisationId, organisationId),
            Filter.Eq(a => a.RegistrationId, RegistrationId),
            Filter.Eq(a => a.MaterialType, Material),
            Filter.Eq(a => a.Year, AccreditationYear),
            Filter.Ne(a => a.ApplicationStatus, ApplicationStatus.Withdrawn)
        );

    private static FilterDefinition<AccreditationApplicationModel> GuardedTarget(
        string organisationId,
        long version
    ) =>
        Filter.And(
            Filter.Eq(a => a.Id, TargetId),
            Filter.Eq(a => a.Version, version),
            Filter.Eq(a => a.ApplicationStatus, ApplicationStatus.Saved),
            Filter.Eq(a => a.OrganisationId, organisationId),
            Filter.Eq(a => a.RegistrationId, RegistrationId),
            Filter.Eq(a => a.MaterialType, Material),
            Filter.Eq(a => a.Year, AccreditationYear),
            Filter.In(a => a.OrgId, [null, OrgId])
        );

    private static string OrgIdText(int? orgId) =>
        orgId?.ToString(CultureInfo.InvariantCulture) ?? "null";
}
