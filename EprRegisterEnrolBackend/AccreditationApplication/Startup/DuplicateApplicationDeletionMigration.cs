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

    // Who spot-checked the record against the dry-run output. Required before Apply takes effect.
    // It is a person's name, so it is never logged: the authorisation is recorded where it is set,
    // in cdp-app-config's history.
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
// deleted. Each step is guarded, so a retry or a second instance does nothing further: if another
// instance deleted the target first, this one reports it already deleted.
//
// After an error that was not a failed check (a commit can fail with an unknown result), the
// target is re-read: gone means the delete committed, present means it rolled back, and a failed
// re-read or a cancellation part-way means the outcome is unknown and the record must be checked.
//
// Logging: every line starts RA-788 and states the mode. Only ids, statuses, years, materials and
// counts are logged - not the reviewer's name, nor any string read from the database - and an
// exception is logged by its type only, as its message could carry personal data.
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
        OutcomeUnknown,
    }

    // Test seams, in production code because the outcomes they simulate cannot otherwise be
    // reached against a real mongod; acceptable only because this class is temporary and goes with
    // its tests. They run between the checks and the transaction; inside the transaction straight
    // after the delete (given its session, so a write can be made inside it); and before the
    // re-read that verifies an errored transaction. So a concurrent edit or delete, a failed
    // in-transaction check, a fault part-way and a failed re-read can each be simulated.
    internal Func<CancellationToken, Task>? BeforeDeleteForTest { get; set; }
    internal Func<IClientSessionHandle, CancellationToken, Task>? AfterDeleteForTest { get; set; }
    internal Func<CancellationToken, Task>? BeforeVerifyForTest { get; set; }

    private static FilterDefinitionBuilder<AccreditationApplicationModel> Filters =>
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
            // The mode is logged on a dry run and an apply alike, so the record of a failure shows
            // which run it was. The type only: a message could carry personal data. Every check
            // runs before the transaction and the delete is guarded, so the next run starts again
            // safely.
            logger.LogError(
                "RA-788 {Mode:l}: duplicate application deletion failed: error={Error}",
                ModeOf(options.Value),
                ex.GetType().Name
            );
        }
    }

    // Apply takes effect only with a named reviewer: the reviewer, or null for a dry run.
    private static string? ReviewerOf(DuplicateApplicationDeletionOptions settings) =>
        settings.Apply && !string.IsNullOrWhiteSpace(settings.SpotCheckConfirmedBy)
            ? settings.SpotCheckConfirmedBy
            : null;

    private static string ModeOf(DuplicateApplicationDeletionOptions settings) =>
        ReviewerOf(settings) is null ? DryRunMode : ApplyMode;

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
        // As read by the checks: its Version is what the delete is guarded on.
        var target = check.Target!;

        if (confirmedBy is null)
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "RA-788 {Mode:l}: would delete application {TargetId} (status={Status} "
                        + "version={Version} orgId={TargetOrgId}) and keep {KeeperId}",
                    mode,
                    TargetId,
                    target.ApplicationStatus,
                    target.Version,
                    OrgIdText(target.OrgId),
                    KeeperId
                );
            return RunResult.DryRun;
        }

        return await DeleteAsync(factory, collection, target, cancellationToken);
    }

    // ReviewerOf, plus an error when Apply is set without a reviewer (the run stays a dry run).
    private string? AuthorisedReviewer(DuplicateApplicationDeletionOptions settings)
    {
        var reviewer = ReviewerOf(settings);
        if (settings.Apply && reviewer is null)
            logger.LogError(
                "RA-788 {Mode:l}: Apply is set but SpotCheckConfirmedBy is not, so running as a dry "
                    + "run. Set it to whoever spot-checked the record.",
                DryRunMode
            );
        return reviewer;
    }

    private sealed record CheckOutcome(RunResult? Result, AccreditationApplicationModel? Target);

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
                return new CheckOutcome(RunResult.AlreadyDone, null);
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
                    $"registrationMatches={target.RegistrationId == RegistrationId} ",
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
                    $"registrationMatches={keeper.RegistrationId == RegistrationId} ",
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
        return new CheckOutcome(null, target);
    }

    // The reason holds only ids, statuses, years, materials, counts and booleans - nothing read
    // from the database as free text - so it is safe to render as a literal (see the class comment).
    private CheckOutcome Abort(string mode, string reason)
    {
        logger.LogError(
            "RA-788 {Mode:l}: aborted, nothing has been written: {Reason:l}",
            mode,
            reason
        );
        return new CheckOutcome(RunResult.Aborted, null);
    }

    private static string Invariant(params FormattableString[] parts) =>
        string.Concat(parts.Select(p => p.ToString(CultureInfo.InvariantCulture)));

    private async Task<RunResult> DeleteAsync(
        IMongoDbClientFactory factory,
        IMongoCollection<AccreditationApplicationModel> collection,
        AccreditationApplicationModel target,
        CancellationToken cancellationToken
    )
    {
        var version = target.Version;
        if (BeforeDeleteForTest is { } before)
            await before(cancellationToken);

        // Set when an in-transaction check fails; throwing then aborts the transaction, and the
        // reason (ids and counts only) is what is logged rather than an exception's message.
        string? rollbackReason = null;
        // Set when the guarded delete matched nothing because the target is already gone: another
        // instance deleted it after this one's checks.
        var deletedElsewhere = false;
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
                    deletedElsewhere = false;

                    // 1. Exactly the record checked, unchanged since.
                    var deleted = await collection.DeleteOneAsync(
                        s,
                        GuardedTarget(target.OrganisationId, version),
                        cancellationToken: token
                    );
                    if (deleted.DeletedCount != 1)
                    {
                        deletedElsewhere =
                            await collection.CountDocumentsAsync(
                                s,
                                Filters.Eq(a => a.Id, TargetId),
                                cancellationToken: token
                            ) == 0;
                        FailCheck(
                            deletedElsewhere
                                ? "the target was deleted by another run after the checks"
                                : Invariant(
                                    $"deleted {deleted.DeletedCount} records; the target changed after the checks"
                                )
                        );
                    }

                    if (AfterDeleteForTest is { } after)
                        await after(s, token);

                    // 2. The keeper is still live.
                    var keeperLive = await collection.CountDocumentsAsync(
                        s,
                        Filters.And(
                            Filters.Eq(a => a.Id, KeeperId),
                            Filters.Ne(a => a.ApplicationStatus, ApplicationStatus.Withdrawn)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping part-way: the commit may or may not have happened, and a re-read now would be
            // cancelled too.
            LogOutcomeUnknown("the run was cancelled during the delete");
            throw;
        }
        catch (Exception ex)
        {
            // Nothing for this run to do: the other run's transaction made the same checks.
            if (deletedElsewhere)
            {
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                        "RA-788 {Mode:l}: application {TargetId} already deleted by another run "
                            + "after the checks; skipped",
                        ApplyMode,
                        TargetId
                    );
                return RunResult.AlreadyDone;
            }

            // A failed check always rolls back.
            if (rollbackReason is not null)
            {
                LogRolledBack(rollbackReason);
                return RunResult.RolledBack;
            }

            // Any other error may have come from the commit itself (an unknown commit result), so
            // what is actually in the collection is reported rather than assumed.
            switch (await VerifyAsync(collection, ex, cancellationToken))
            {
                case Verified.Deleted:
                    LogApplied(version);
                    return RunResult.Applied;
                case Verified.NotDeleted:
                    LogRolledBack(ex.GetType().Name);
                    return RunResult.RolledBack;
                default:
                    return RunResult.OutcomeUnknown;
            }
        }

        LogApplied(version);
        return RunResult.Applied;
    }

    private enum Verified
    {
        Deleted,
        NotDeleted,
        Unknown,
    }

    // After an error that was not a failed check: whether the delete committed, judged by whether
    // the target is still there. If the re-read fails too, the outcome is unknown.
    private async Task<Verified> VerifyAsync(
        IMongoCollection<AccreditationApplicationModel> collection,
        Exception error,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (BeforeVerifyForTest is { } beforeVerify)
                await beforeVerify(cancellationToken);
            if (await FindByIdAsync(collection, TargetId, cancellationToken) is not null)
                return Verified.NotDeleted;

            logger.LogError(
                "RA-788 {Mode:l}: the transaction reported error={Error}, but target "
                    + "{TargetId} is gone, so the delete committed",
                ApplyMode,
                error.GetType().Name,
                TargetId
            );
            return Verified.Deleted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogOutcomeUnknown(
                Invariant(
                    $"the transaction reported error={error.GetType().Name} and the run was cancelled before the target could be re-read"
                )
            );
            throw;
        }
        catch (Exception readError)
        {
            LogOutcomeUnknown(
                Invariant(
                    $"the transaction reported error={error.GetType().Name} and the target could not be re-read (error={readError.GetType().Name})"
                )
            );
            return Verified.Unknown;
        }
    }

    // The reason holds only exception type names and fixed text.
    private void LogOutcomeUnknown(string reason) =>
        logger.LogError(
            "RA-788 {Mode:l}: outcome unknown, the delete may or may not have committed: {Reason:l}. "
                + "Check whether application {TargetId} still exists; a re-run reports the real "
                + "state (it logs \"already deleted\" if the delete committed).",
            ApplyMode,
            reason,
            TargetId
        );

    // The reason holds only ids, counts, exception type names and fixed text.
    private void LogRolledBack(string reason) =>
        logger.LogError(
            "RA-788 {Mode:l}: rolled back, nothing has been deleted. target={TargetId} "
                + "reason={Reason:l}",
            ApplyMode,
            TargetId,
            reason
        );

    // The reviewer is not logged (see SpotCheckConfirmedBy).
    private void LogApplied(long version)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-788 APPLIED: deleted application {TargetId} (version={Version}); kept "
                    + "{KeeperId}. Spot check confirmed",
                TargetId,
                version,
                KeeperId
            );
    }

    private static async Task<AccreditationApplicationModel?> FindByIdAsync(
        IMongoCollection<AccreditationApplicationModel> collection,
        ObjectId id,
        CancellationToken cancellationToken
    ) =>
        await collection
            .Find(Filters.Eq(a => a.Id, id))
            .FirstOrDefaultAsync(cancellationToken);

    // The identity the target, the keeper and the group share. One definition each for the
    // in-memory checks and the Mongo filters, so the predicates cannot drift apart.
    private static bool HasGroupIdentity(AccreditationApplicationModel application) =>
        application.RegistrationId == RegistrationId
        && application.Year == AccreditationYear
        && application.MaterialType == Material;

    // The seed endpoint's grouping: HasGroupIdentity plus the ReEx organisation.
    private static FilterDefinition<AccreditationApplicationModel> InGroup(string organisationId) =>
        Filters.And(
            Filters.Eq(a => a.OrganisationId, organisationId),
            Filters.Eq(a => a.RegistrationId, RegistrationId),
            Filters.Eq(a => a.MaterialType, Material),
            Filters.Eq(a => a.Year, AccreditationYear)
        );

    private static bool IsTarget(AccreditationApplicationModel target) =>
        HasGroupIdentity(target)
        && target.OrgId is null or OrgId
        && target.ApplicationStatus == ApplicationStatus.Saved;

    private static bool IsKeeper(
        AccreditationApplicationModel keeper,
        AccreditationApplicationModel target
    ) =>
        HasGroupIdentity(keeper)
        && keeper.OrganisationId == target.OrganisationId
        && keeper.OrgId == OrgId
        && keeper.ApplicationStatus == ApplicationStatus.Updated;

    private static FilterDefinition<AccreditationApplicationModel> LiveInGroup(
        string organisationId
    ) =>
        Filters.And(
            InGroup(organisationId),
            Filters.Ne(a => a.ApplicationStatus, ApplicationStatus.Withdrawn)
        );

    private static FilterDefinition<AccreditationApplicationModel> GuardedTarget(
        string organisationId,
        long version
    ) =>
        Filters.And(
            Filters.Eq(a => a.Id, TargetId),
            Filters.Eq(a => a.Version, version),
            Filters.Eq(a => a.ApplicationStatus, ApplicationStatus.Saved),
            InGroup(organisationId),
            Filters.In(a => a.OrgId, [null, OrgId])
        );

    private static string OrgIdText(int? orgId) =>
        orgId?.ToString(CultureInfo.InvariantCulture) ?? "null";
}