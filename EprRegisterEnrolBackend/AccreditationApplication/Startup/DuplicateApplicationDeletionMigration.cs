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

    // Off (with Enabled on): dry run - logs what it would delete, writes nothing. On, it deletes only
    // when SpotCheckConfirmedBy is set and ExpectedDeletions equals the candidates this run finds.
    public bool Apply { get; set; }

    // Who spot-checked this environment's dry run. Required before Apply takes effect.
    // It is a person's name, so it is never logged: the authorisation is recorded where it is set,
    // in cdp-app-config's history.
    public string? SpotCheckConfirmedBy { get; set; }

    // The candidate count from this environment's reviewed dry run. If this run finds a different
    // number the run stays a dry run (with an error log), so the reviewed set is exactly what is
    // deleted.
    public int? ExpectedDeletions { get; set; }
}

// RA-788 (INC2560371): duplicate live applications exist for the same registration, because the
// seed endpoint's read-then-create is not atomic. This one-off finds every duplicate in the legacy
// accreditationApplications collection and deletes the abandoned draft, keeping the other
// record(s). The domain model is out of scope. Records linked to a deleted one are left in place
// and not checked. A Saved application has never been submitted to case management. There is no
// backup copy: a restore comes from the CDP backup.
//
// Group: live (not Withdrawn) applications are grouped on organisationId, organisationName,
// materialType, registrationReference and year. A record with any of the four text fields null or
// empty is not grouped (counted as ungrouped-missing-key). Groups of one are not duplicates.
//
// Candidate: no OrgId and status Saved. Keeper: every other record in the group. A group's
// candidates are deleted only when it also has a keeper (skipped-no-keeper otherwise); a group with
// no candidate is logged for a human (skipped-no-candidate).
//
// Settings: Enabled alone is a dry run. Apply deletes only with SpotCheckConfirmedBy set and
// ExpectedDeletions equal to the number of candidates found this run.
//
// Delete: one transaction per group. Each candidate is DeleteOne'd guarded on its scanned Version,
// Saved, OrgId null and the five key values, and must delete exactly 1; then every keeper must
// still be live and the group must hold exactly as many live records as it has keepers. Any failure
// rolls that group back and the run moves on. Each delete is guarded, so a re-run or a second
// instance finds nothing left to delete in a finished group.
//
// After an error that was not a failed check (a commit can fail with an unknown result), the
// group's candidates are re-read: all gone means the delete committed, all present means it rolled
// back, anything else - or a failed re-read, or a cancellation - is outcome unknown.
//
// Logging: every line starts RA-788 and states the mode. organisationName and organisationId are
// never logged (a business name can be a sole trader's own name), nor the reviewer. An exception is
// logged by its type only, as its message could carry personal data.
//
// A BackgroundService, so a failure here logs rather than crash-looping the host. The Mongo client
// is resolved only once the migration is enabled.
//
// Temporary: removed, with its tests, Program.cs registration, appsettings section, Serilog
// override and cdp-app-config variables, once dev and prod are confirmed clean.
public class DuplicateApplicationDeletionMigration(
    IServiceProvider serviceProvider,
    IOptions<DuplicateApplicationDeletionOptions> options,
    ILogger<DuplicateApplicationDeletionMigration> logger
) : BackgroundService
{
    private const string DryRunMode = "DRY RUN (nothing will be written)";
    private const string ApplyMode = "APPLY";

    private static readonly TimeSpan ScanMaxTime = TimeSpan.FromMinutes(2);

    internal enum RunMode
    {
        Disabled,
        DryRun,
        Apply,
    }

    // What a run did, so tests (and the one summary log line) can assert it.
    internal sealed record RunSummary(
        RunMode Mode,
        int Groups = 0,
        int Candidates = 0,
        int WouldDelete = 0,
        int Deleted = 0,
        int SkippedNoKeeper = 0,
        int SkippedNoCandidate = 0,
        int UngroupedMissingKey = 0,
        int RolledBack = 0,
        int OutcomeUnknown = 0
    );

    // Test seams, in production code because the outcomes they simulate cannot otherwise be
    // reached against a real mongod; acceptable only because this class is temporary and goes with
    // its tests. Each gets the group's candidate ids so a test can target one group. They run
    // between the scan and a group's transaction; inside the transaction straight after the
    // deletes (given its session, so a write can be made inside it); and before the re-read that
    // verifies an errored transaction.
    internal Func<IReadOnlyList<ObjectId>, CancellationToken, Task>? BeforeDeleteForTest { get; set; }
    internal Func<
        IReadOnlyList<ObjectId>,
        IClientSessionHandle,
        CancellationToken,
        Task
    >? AfterDeleteForTest
    { get; set; }
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
            // The type only: a message could carry personal data. The scan is read only and every
            // delete is guarded, so the next run starts again safely.
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
    internal async Task<RunSummary> RunAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return new RunSummary(RunMode.Disabled);

        var factory = serviceProvider.GetRequiredService<IMongoDbClientFactory>();
        var collection = factory.GetCollection<AccreditationApplicationModel>(
            DomainModelCollections.LegacyApplications
        );

        var scan = await ScanAsync(collection, cancellationToken);
        var groups = scan.Groups;
        var candidates = groups
            .Where(g => g.Outcome == GroupOutcome.Delete)
            .Sum(g => g.Candidates.Count);

        var apply = DecideApply(options.Value, candidates);
        var mode = apply ? ApplyMode : DryRunMode;
        var summary = new RunSummary(
            apply ? RunMode.Apply : RunMode.DryRun,
            Groups: groups.Count,
            Candidates: candidates,
            SkippedNoKeeper: groups.Count(g => g.Outcome == GroupOutcome.NoKeeper),
            SkippedNoCandidate: groups.Count(g => g.Outcome == GroupOutcome.NoCandidate),
            UngroupedMissingKey: scan.UngroupedMissingKey
        );

        foreach (var group in groups)
            LogGroup(mode, group);

        if (!apply)
        {
            summary = summary with { WouldDelete = candidates };
            LogSummary(mode, summary);
            return summary;
        }

        foreach (var group in groups.Where(g => g.Outcome == GroupOutcome.Delete))
        {
            summary = await DeleteGroupAsync(factory, collection, group, cancellationToken) switch
            {
                DeleteResult.Deleted => summary with
                {
                    Deleted = summary.Deleted + group.Candidates.Count,
                },
                DeleteResult.RolledBack => summary with { RolledBack = summary.RolledBack + 1 },
                _ => summary with { OutcomeUnknown = summary.OutcomeUnknown + 1 },
            };
        }

        LogSummary(mode, summary);
        return summary;
    }

    // Apply needs Apply, a reviewer and an ExpectedDeletions equal to what this run found. Anything
    // short of that is a dry run, with an error log when Apply was asked for.
    private bool DecideApply(DuplicateApplicationDeletionOptions settings, int candidates)
    {
        if (!settings.Apply)
            return false;

        if (ReviewerOf(settings) is null)
        {
            logger.LogError(
                "RA-788 {Mode:l}: Apply is set but SpotCheckConfirmedBy is not, so running as a dry "
                    + "run. Set it to whoever spot-checked this environment's dry run.",
                DryRunMode
            );
            return false;
        }

        if (settings.ExpectedDeletions != candidates)
        {
            logger.LogError(
                "RA-788 {Mode:l}: Apply is set but ExpectedDeletions={Expected} does not match the "
                    + "{Candidates} candidates found this run, so running as a dry run. Review the "
                    + "dry run again and set ExpectedDeletions to the reviewed count.",
                DryRunMode,
                settings.ExpectedDeletions?.ToString(CultureInfo.InvariantCulture) ?? "unset",
                candidates
            );
            return false;
        }

        return true;
    }

    // ---- Scan (read only) ----

    private enum GroupOutcome
    {
        Delete,
        NoKeeper,
        NoCandidate,
    }

    // The five fields that make two applications duplicates. Never logged in full.
    private sealed record GroupKey(
        string OrganisationId,
        string OrganisationName,
        MaterialType MaterialType,
        string RegistrationReference,
        int Year
    );

    private sealed record DuplicateGroup(
        GroupKey Key,
        IReadOnlyList<AccreditationApplicationModel> Candidates,
        IReadOnlyList<AccreditationApplicationModel> Keepers
    )
    {
        public GroupOutcome Outcome =>
            Candidates.Count == 0 ? GroupOutcome.NoCandidate
            : Keepers.Count == 0 ? GroupOutcome.NoKeeper
            : GroupOutcome.Delete;
    }

    private sealed record ScanResult(IReadOnlyList<DuplicateGroup> Groups, int UngroupedMissingKey);

    private static bool IsCandidate(AccreditationApplicationModel a) =>
        a.OrgId is null && a.ApplicationStatus == ApplicationStatus.Saved;

    private static bool HasKey(AccreditationApplicationModel a) =>
        !string.IsNullOrEmpty(a.OrganisationId)
        && !string.IsNullOrEmpty(a.OrganisationName)
        && !string.IsNullOrEmpty(a.RegistrationReference);

    private static GroupKey KeyOf(AccreditationApplicationModel a) =>
        new(a.OrganisationId, a.OrganisationName!, a.MaterialType, a.RegistrationReference!, a.Year);

    // Only the fields the rule and the logs need are read, with a maxTime. The grouping is done in
    // memory rather than in an aggregation: this is a one-off startup scan, and it keeps the key
    // comparison exact.
    private static async Task<ScanResult> ScanAsync(
        IMongoCollection<AccreditationApplicationModel> collection,
        CancellationToken cancellationToken
    )
    {
        var projection = Builders<AccreditationApplicationModel>
            .Projection.Include(a => a.Id)
            .Include(a => a.OrgId)
            .Include(a => a.ApplicationStatus)
            .Include(a => a.Year)
            .Include(a => a.Version)
            .Include(a => a.CreatedAt)
            .Include(a => a.DateLastEdited)
            .Include(a => a.RegistrationReference)
            .Include(a => a.OrganisationId)
            .Include(a => a.OrganisationName)
            .Include(a => a.MaterialType);

        var live = await collection
            .Find(
                Filters.Ne(a => a.ApplicationStatus, ApplicationStatus.Withdrawn),
                new FindOptions { MaxTime = ScanMaxTime }
            )
            .Project<AccreditationApplicationModel>(projection)
            .ToListAsync(cancellationToken);

        var ungrouped = live.Count(a => !HasKey(a));
        var groups = live
            .Where(HasKey)
            .GroupBy(KeyOf)
            .Where(g => g.Count() >= 2)
            .Select(g => new DuplicateGroup(
                g.Key,
                g.Where(IsCandidate).OrderBy(a => a.Id).ToList(),
                g.Where(a => !IsCandidate(a)).OrderBy(a => a.Id).ToList()
            ))
            .ToList();
        return new ScanResult(groups, ungrouped);
    }

    // ---- Logging ----

    // Only registration reference, material, year, ids, statuses, versions and dates: never the
    // organisation name or id.
    private void LogGroup(string mode, DuplicateGroup group)
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;

        logger.LogInformation(
            "RA-788 {Mode:l}: group registration={Registration} material={Material} year={Year} "
                + "outcome={Outcome:l} candidates={Candidates} keepers={Keepers}",
            mode,
            group.Key.RegistrationReference,
            group.Key.MaterialType,
            group.Key.Year,
            group.Outcome switch
            {
                GroupOutcome.Delete => mode == ApplyMode ? "delete" : "would-delete",
                GroupOutcome.NoKeeper => "skipped-no-keeper",
                _ => "skipped-no-candidate",
            },
            group.Candidates.Count,
            group.Keepers.Count
        );
        foreach (var c in group.Candidates)
            logger.LogInformation(
                "RA-788 {Mode:l}: candidate {Id} status={Status} version={Version} "
                    + "createdAt={CreatedAt:O} dateLastEdited={DateLastEdited:O}",
                mode,
                c.Id,
                c.ApplicationStatus,
                c.Version,
                c.CreatedAt,
                c.DateLastEdited
            );
        foreach (var k in group.Keepers)
            logger.LogInformation(
                "RA-788 {Mode:l}: keeper {Id} status={Status} hasOrgId={HasOrgId}",
                mode,
                k.Id,
                k.ApplicationStatus,
                k.OrgId is not null
            );
    }

    private void LogSummary(string mode, RunSummary s) =>
        logger.LogInformation(
            "RA-788 {Mode:l}: summary groups={Groups} candidates={Candidates} "
                + "would-delete={WouldDelete} deleted={Deleted} skipped-no-keeper={NoKeeper} "
                + "skipped-no-candidate={NoCandidate} ungrouped-missing-key={Ungrouped} "
                + "rolled-back={RolledBack} outcome-unknown={Unknown}",
            mode,
            s.Groups,
            s.Candidates,
            s.WouldDelete,
            s.Deleted,
            s.SkippedNoKeeper,
            s.SkippedNoCandidate,
            s.UngroupedMissingKey,
            s.RolledBack,
            s.OutcomeUnknown
        );

    private static string Invariant(FormattableString text) =>
        text.ToString(CultureInfo.InvariantCulture);

    // ---- Delete ----

    private enum DeleteResult
    {
        Deleted,
        RolledBack,
        Unknown,
    }

    private async Task<DeleteResult> DeleteGroupAsync(
        IMongoDbClientFactory factory,
        IMongoCollection<AccreditationApplicationModel> collection,
        DuplicateGroup group,
        CancellationToken cancellationToken
    )
    {
        if (BeforeDeleteForTest is { } before)
            await before(CandidateIdList(group), cancellationToken);

        var attempt = new DeleteAttempt();
        try
        {
            using var session = await factory
                .GetClient()
                .StartSessionAsync(cancellationToken: cancellationToken);
            await session.WithTransactionAsync(
                (s, token) => DeleteInTransactionAsync(collection, s, group, attempt, token),
                DomainModelTransactions.Options,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping part-way: the commit may or may not have happened, and a re-read now would be
            // cancelled too.
            LogOutcomeUnknown(group, "the run was cancelled during the delete");
            throw;
        }
        catch (Exception ex)
        {
            return await OutcomeAfterErrorAsync(collection, group, attempt, ex, cancellationToken);
        }

        LogApplied(group);
        return DeleteResult.Deleted;
    }

    // Why the last transaction attempt failed one of its own checks, if it did. Failing a check
    // throws, which aborts the transaction; the reason (ids and counts only) is what is logged,
    // rather than an exception's message.
    private sealed class DeleteAttempt
    {
        public string? RollbackReason { get; private set; }

        // The driver re-runs the transaction body on a transient error, so each attempt starts clean.
        public void Reset() => RollbackReason = null;

        public void Fail(string reason)
        {
            RollbackReason = reason;
            throw new InvalidOperationException(reason);
        }
    }

    private async Task<bool> DeleteInTransactionAsync(
        IMongoCollection<AccreditationApplicationModel> collection,
        IClientSessionHandle session,
        DuplicateGroup group,
        DeleteAttempt attempt,
        CancellationToken token
    )
    {
        attempt.Reset();

        // 1. Exactly the records scanned, unchanged since.
        foreach (var candidate in group.Candidates)
        {
            var deleted = await collection.DeleteOneAsync(
                session,
                GuardedCandidate(group.Key, candidate),
                cancellationToken: token
            );
            if (deleted.DeletedCount != 1)
                attempt.Fail(
                    Invariant(
                        $"candidate {candidate.Id} deleted {deleted.DeletedCount} records; it was removed or changed after the scan"
                    )
                );
        }

        if (AfterDeleteForTest is { } after)
            await after(CandidateIdList(group), session, token);

        // 2. Every keeper is still live.
        var keeperIds = group.Keepers.Select(k => k.Id).ToList();
        var keepersLive = await collection.CountDocumentsAsync(
            session,
            Filters.And(
                Filters.In(a => a.Id, keeperIds.Cast<ObjectId?>()),
                Filters.Ne(a => a.ApplicationStatus, ApplicationStatus.Withdrawn)
            ),
            cancellationToken: token
        );
        if (keepersLive != keeperIds.Count)
            attempt.Fail("a keeper is no longer live");

        // 3. Exactly the keepers are left live in the group.
        var live = await collection.CountDocumentsAsync(
            session,
            LiveInGroup(group.Key),
            cancellationToken: token
        );
        if (live != keeperIds.Count)
            attempt.Fail(
                Invariant(
                    $"the group would hold {live} live applications; expected {keeperIds.Count}"
                )
            );
        return true;
    }

    private async Task<DeleteResult> OutcomeAfterErrorAsync(
        IMongoCollection<AccreditationApplicationModel> collection,
        DuplicateGroup group,
        DeleteAttempt attempt,
        Exception error,
        CancellationToken cancellationToken
    )
    {
        // A failed check always rolls back.
        if (attempt.RollbackReason is { } reason)
        {
            LogRolledBack(group, reason);
            return DeleteResult.RolledBack;
        }

        // Any other error may have come from the commit itself (an unknown commit result), so
        // what is actually in the collection is reported rather than assumed.
        try
        {
            if (BeforeVerifyForTest is { } beforeVerify)
                await beforeVerify(cancellationToken);
            var ids = CandidateIdList(group);
            var remaining = await collection.CountDocumentsAsync(
                Filters.In(a => a.Id, ids.Cast<ObjectId?>()),
                cancellationToken: cancellationToken
            );
            if (remaining == ids.Count)
            {
                LogRolledBack(group, error.GetType().Name);
                return DeleteResult.RolledBack;
            }
            if (remaining == 0)
            {
                logger.LogError(
                    "RA-788 {Mode:l}: the transaction reported error={Error}, but the candidates "
                        + "{Ids} are gone, so the delete committed",
                    ApplyMode,
                    error.GetType().Name,
                    CandidateIds(group)
                );
                LogApplied(group);
                return DeleteResult.Deleted;
            }
            LogOutcomeUnknown(
                group,
                Invariant(
                    $"the transaction reported error={error.GetType().Name} and only some candidates remain"
                )
            );
            return DeleteResult.Unknown;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogOutcomeUnknown(
                group,
                Invariant(
                    $"the transaction reported error={error.GetType().Name} and the run was cancelled before the candidates could be re-read"
                )
            );
            throw;
        }
        catch (Exception readError)
        {
            LogOutcomeUnknown(
                group,
                Invariant(
                    $"the transaction reported error={error.GetType().Name} and the candidates could not be re-read (error={readError.GetType().Name})"
                )
            );
            return DeleteResult.Unknown;
        }
    }

    private static List<ObjectId> CandidateIdList(DuplicateGroup group) =>
        group.Candidates.Select(c => c.Id!.Value).ToList();

    private static string CandidateIds(DuplicateGroup group) =>
        string.Join(",", group.Candidates.Select(c => c.Id.ToString()));

    // The reason holds only exception type names and fixed text.
    private void LogOutcomeUnknown(DuplicateGroup group, string reason) =>
        logger.LogError(
            "RA-788 {Mode:l}: outcome unknown, the delete may or may not have committed: {Reason:l}. "
                + "Check whether applications {Ids} still exist; a re-run reports the real state.",
            ApplyMode,
            reason,
            CandidateIds(group)
        );

    // The reason holds only ids, counts, exception type names and fixed text.
    private void LogRolledBack(DuplicateGroup group, string reason) =>
        logger.LogError(
            "RA-788 {Mode:l}: rolled back, nothing has been deleted. candidates={Ids} "
                + "reason={Reason:l}",
            ApplyMode,
            CandidateIds(group),
            reason
        );

    // The reviewer is not logged (see SpotCheckConfirmedBy).
    private void LogApplied(DuplicateGroup group)
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;
        var keepers = string.Join(",", group.Keepers.Select(k => k.Id.ToString()));
        foreach (var c in group.Candidates)
            logger.LogInformation(
                "RA-788 APPLIED: deleted application {Id} (version={Version}); group kept "
                    + "{Keepers}. Spot check confirmed",
                c.Id,
                c.Version,
                keepers
            );
    }

    // ---- Filters ----

    private static FilterDefinition<AccreditationApplicationModel> InGroup(GroupKey key) =>
        Filters.And(
            Filters.Eq(a => a.OrganisationId, key.OrganisationId),
            Filters.Eq(a => a.OrganisationName, key.OrganisationName),
            Filters.Eq(a => a.MaterialType, key.MaterialType),
            Filters.Eq(a => a.RegistrationReference, key.RegistrationReference),
            Filters.Eq(a => a.Year, key.Year)
        );

    private static FilterDefinition<AccreditationApplicationModel> LiveInGroup(GroupKey key) =>
        Filters.And(
            InGroup(key),
            Filters.Ne(a => a.ApplicationStatus, ApplicationStatus.Withdrawn)
        );

    private static FilterDefinition<AccreditationApplicationModel> GuardedCandidate(
        GroupKey key,
        AccreditationApplicationModel candidate
    ) =>
        Filters.And(
            Filters.Eq(a => a.Id, candidate.Id),
            Filters.Eq(a => a.Version, candidate.Version),
            Filters.Eq(a => a.ApplicationStatus, ApplicationStatus.Saved),
            Filters.Eq(a => a.OrgId, null),
            InGroup(key)
        );
}