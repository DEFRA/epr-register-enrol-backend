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
    // when SpotCheckConfirmedBy is set and ExpectedDeletions and ExpectedApplicationIds both match
    // the candidates this run finds.
    public bool Apply { get; set; }

    // Who spot-checked this environment's dry run. Required before Apply takes effect.
    // It is a person's name, so it is never logged: the authorisation is recorded where it is set,
    // in cdp-app-config's history.
    public string? SpotCheckConfirmedBy { get; set; }

    // The candidate count from this environment's reviewed dry run. If this run finds a different
    // number the run stays a dry run (with an error log).
    public int? ExpectedDeletions { get; set; }

    // The candidate ids from this environment's reviewed dry run, comma separated (the
    // candidate-ids= value of its summary line). Apply proceeds only if this run's candidate ids are
    // exactly this set; otherwise the run stays a dry run (with an error log). The count alone is
    // not enough: one candidate could stop qualifying and another appear, leaving the count the same.
    public string? ExpectedApplicationIds { get; set; }
}

// RA-788 (INC2560371): duplicate live applications exist for the same registration, because the
// seed endpoint's read-then-create is not atomic. This one-off finds every duplicate in the legacy
// accreditationApplications collection and deletes the abandoned draft, keeping the other
// record(s). The domain model is out of scope: any domain-model copy of a deleted application is
// not deleted. Records linked to a deleted one are left in place and not checked. A Saved
// application has never been submitted to case management. There is no backup copy: a restore
// comes from the CDP backup.
//
// Group: live (not Withdrawn) applications are grouped on organisationId, organisationName,
// registrationId, materialType and year. Records with different registration references still
// group. A record with organisationId, organisationName or registrationId null or empty is not
// grouped (counted as ungrouped-missing-key). Groups of one are not duplicates.
//
// Candidate: no OrgId and status Saved. Keeper: every other record in the group. A group's
// candidates are deleted only when it also has a keeper (skipped-no-keeper otherwise); a group with
// no candidate is logged for a human (skipped-no-candidate).
//
// Settings: Enabled alone is a dry run. Apply deletes only with SpotCheckConfirmedBy set,
// ExpectedDeletions equal to the number of candidates found this run and ExpectedApplicationIds equal
// to the set of their ids. Apply with no candidates found is treated as already done (an info log).
//
// Delete: one transaction per group. Each candidate is DeleteOne'd guarded on its scanned Version,
// Saved, OrgId null and the five key values, and must delete exactly 1; then every keeper must
// still be live and the group must hold exactly as many live records as it has keepers. Any failure
// rolls that group back and the run moves on. Each delete is guarded, so a re-run or a second
// instance finds nothing left to delete in a finished group; a group whose candidates are all
// already gone is logged as deleted by another run, not as a failure.
//
// After an error that was not a failed check (a commit can fail with an unknown result), the
// group's candidates are re-read: all gone means the delete committed, all present means it rolled
// back, anything else - or a failed re-read, or a cancellation - is outcome unknown.
//
// Logging: every line starts RA-788 and states the mode. The summary (counts and candidate ids) is
// logged first, then one line per group (with its candidates and keepers), then an outcome line.
// Each group line carries the group's organisationId and organisationName so the set to be deleted
// can be reviewed. The reviewer and applicant contact details are never logged. An exception is
// logged by its type only, as its message could carry personal data.
//
// A BackgroundService, so a failure here (including settings that fail to bind) logs rather than
// crash-looping the host. The Mongo client is resolved only once the migration is enabled.
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

    // What a run did, so tests (and the log lines) can assert it. The *Records counts are
    // applications; the *Groups counts are groups.
    internal sealed record RunSummary(
        RunMode Mode,
        int Groups = 0,
        int Candidates = 0,
        int WouldDeleteRecords = 0,
        int DeletedRecords = 0,
        int SkippedNoKeeper = 0,
        int SkippedNoCandidate = 0,
        int UngroupedMissingKey = 0,
        int RolledBackGroups = 0,
        int OutcomeUnknownGroups = 0,
        int DeletedElsewhereGroups = 0
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

    // Live: not Withdrawn.
    private static FilterDefinition<AccreditationApplicationModel> Live =>
        Filters.Ne(a => a.ApplicationStatus, ApplicationStatus.Withdrawn);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is never held up by anything below.
        await Task.Yield();

        // Read once, here: a setting that fails to bind (ExpectedDeletions=one) throws on this read,
        // and nothing below may read it again, or the failure would escape and stop the host.
        DuplicateApplicationDeletionOptions settings;
        try
        {
            settings = options.Value;
        }
        catch (Exception ex)
        {
            // The type only: the message could echo the bad value.
            logger.LogError(
                "RA-788 {Mode:l}: the settings could not be read, so nothing was done: error={Error}",
                DryRunMode,
                ex.GetType().Name
            );
            return;
        }

        try
        {
            await RunAsync(settings, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // The type only: a message could carry personal data. The scan is read only and every
            // delete is guarded, so the next run starts again safely.
            logger.LogError(
                "RA-788 {Mode:l}: duplicate application deletion failed: error={Error}",
                TextOf(HasReviewer(settings) ? RunMode.Apply : RunMode.DryRun),
                ex.GetType().Name
            );
        }
    }

    // Apply takes effect only with a named reviewer.
    private static bool HasReviewer(DuplicateApplicationDeletionOptions settings) =>
        settings.Apply && !string.IsNullOrWhiteSpace(settings.SpotCheckConfirmedBy);

    private static string TextOf(RunMode mode) => mode == RunMode.Apply ? ApplyMode : DryRunMode;

    // Extracted so tests can await the run deterministically - BackgroundService's StartAsync only
    // kicks ExecuteAsync off as a background task.
    internal Task<RunSummary> RunAsync(CancellationToken cancellationToken) =>
        RunAsync(options.Value, cancellationToken);

    private async Task<RunSummary> RunAsync(
        DuplicateApplicationDeletionOptions settings,
        CancellationToken cancellationToken
    )
    {
        if (!settings.Enabled)
            return new RunSummary(RunMode.Disabled);

        var factory = serviceProvider.GetRequiredService<IMongoDbClientFactory>();
        var collection = factory.GetCollection<AccreditationApplicationModel>(
            DomainModelCollections.LegacyApplications
        );

        var scan = await ScanAsync(collection, cancellationToken);
        var groups = scan.Groups;
        var candidateIds = groups
            .Where(g => g.Outcome == GroupOutcome.Delete)
            .SelectMany(g => g.Candidates.Select(c => c.Id!.Value))
            .Order()
            .ToList();

        var decision = DecideApply(settings, candidateIds);
        var mode = decision.Apply ? RunMode.Apply : RunMode.DryRun;
        var summary = new RunSummary(
            mode,
            Groups: groups.Count,
            Candidates: candidateIds.Count,
            SkippedNoKeeper: groups.Count(g => g.Outcome == GroupOutcome.NoKeeper),
            SkippedNoCandidate: groups.Count(g => g.Outcome == GroupOutcome.NoCandidate),
            UngroupedMissingKey: scan.UngroupedMissingKey
        );

        // The summary first, so the counts and ids are in the log even if the detail is not.
        LogSummary(mode, summary, candidateIds);
        LogDecision(decision);
        foreach (var group in groups)
            LogGroup(mode, group);

        if (!decision.Apply)
        {
            summary = summary with { WouldDeleteRecords = candidateIds.Count };
            LogOutcome(mode, summary);
            return summary;
        }

        foreach (var group in groups.Where(g => g.Outcome == GroupOutcome.Delete))
        {
            summary = await DeleteGroupAsync(factory, collection, group, cancellationToken) switch
            {
                DeleteResult.Deleted => summary with
                {
                    DeletedRecords = summary.DeletedRecords + group.Candidates.Count,
                },
                DeleteResult.RolledBack => summary with
                {
                    RolledBackGroups = summary.RolledBackGroups + 1,
                },
                DeleteResult.DeletedElsewhere => summary with
                {
                    DeletedElsewhereGroups = summary.DeletedElsewhereGroups + 1,
                },
                _ => summary with { OutcomeUnknownGroups = summary.OutcomeUnknownGroups + 1 },
            };
        }

        LogOutcome(mode, summary);
        return summary;
    }

    // The decision is logged after the summary, so it is not decided and logged in one step.
    private sealed record ApplyDecision(bool Apply, string? Error = null, string? Note = null);

    // Apply needs Apply, a reviewer, and an ExpectedDeletions and ExpectedApplicationIds equal to what
    // this run found. Anything short of that is a dry run, with an error log when Apply was asked
    // for. No candidates at all is already done, not an error.
    private static ApplyDecision DecideApply(
        DuplicateApplicationDeletionOptions settings,
        IReadOnlyList<ObjectId> found
    )
    {
        if (!settings.Apply)
            return new ApplyDecision(false);

        if (!HasReviewer(settings))
            return new ApplyDecision(
                false,
                Error: "Apply is set but SpotCheckConfirmedBy is not, so running as a dry "
                    + "run. Set it to whoever spot-checked this environment's dry run."
            );

        if (found.Count == 0)
            return new ApplyDecision(
                true,
                Note: "no candidates found, so nothing to delete: already deleted by an earlier "
                    + "or another run, or none to begin with"
            );

        if (settings.ExpectedDeletions != found.Count)
            return new ApplyDecision(
                false,
                Error: Invariant(
                    $"Apply is set but ExpectedDeletions={settings.ExpectedDeletions?.ToString(CultureInfo.InvariantCulture) ?? "unset"} does not match the {found.Count} candidates found this run, so running as a dry run. Review the dry run again and set ExpectedDeletions to the reviewed count."
                )
            );

        var idsError = ExpectedIdsError(settings.ExpectedApplicationIds, found);
        return new ApplyDecision(idsError is null, Error: idsError);
    }

    // Null when ExpectedApplicationIds is exactly the set of candidate ids found.
    private static string? ExpectedIdsError(string? raw, IReadOnlyList<ObjectId> found)
    {
        const string DryRunSuffix =
            " so running as a dry run. Set ExpectedApplicationIds to the candidate-ids= value of "
            + "the reviewed dry run.";

        var entries = (raw ?? string.Empty).Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (entries.Length == 0)
            return "Apply is set but ExpectedApplicationIds is not," + DryRunSuffix;

        var expected = new HashSet<ObjectId>();
        foreach (var entry in entries)
        {
            if (!ObjectId.TryParse(entry, out var id))
                return "Apply is set but ExpectedApplicationIds has a value that is not an "
                    + "application id,"
                    + DryRunSuffix;
            expected.Add(id);
        }

        var unexpected = found.Where(id => !expected.Contains(id)).ToList();
        var missing = expected.Where(id => !found.Contains(id)).Order().ToList();
        if (unexpected.Count == 0 && missing.Count == 0)
            return null;

        return "Apply is set but ExpectedApplicationIds does not match the candidates found this "
            + $"run (found but not expected: [{string.Join(",", unexpected)}]; expected but not "
            + $"found: [{string.Join(",", missing)}]),"
            + DryRunSuffix;
    }

    // ---- Scan (read only) ----

    private enum GroupOutcome
    {
        Delete,
        NoKeeper,
        NoCandidate,
    }

    // The five fields that make two applications duplicates. The registration reference is not one:
    // records with different references still group, and each line logs its own.
    private sealed record GroupKey(
        string OrganisationId,
        string OrganisationName,
        string RegistrationId,
        MaterialType MaterialType,
        int Year
    );

    private sealed record DuplicateGroup(
        GroupKey Key,
        IReadOnlyList<AccreditationApplicationModel> Candidates,
        IReadOnlyList<AccreditationApplicationModel> Keepers
    )
    {
        public GroupOutcome Outcome
        {
            get
            {
                if (Candidates.Count == 0)
                    return GroupOutcome.NoCandidate;
                return Keepers.Count == 0 ? GroupOutcome.NoKeeper : GroupOutcome.Delete;
            }
        }
    }

    private sealed record ScanResult(IReadOnlyList<DuplicateGroup> Groups, int UngroupedMissingKey);

    private static bool IsCandidate(AccreditationApplicationModel a) =>
        a.OrgId is null && a.ApplicationStatus == ApplicationStatus.Saved;

    private static bool HasKey(AccreditationApplicationModel a) =>
        !string.IsNullOrEmpty(a.OrganisationId)
        && !string.IsNullOrEmpty(a.OrganisationName)
        && !string.IsNullOrEmpty(a.RegistrationId);

    private static GroupKey KeyOf(AccreditationApplicationModel a) =>
        new(a.OrganisationId, a.OrganisationName!, a.RegistrationId!, a.MaterialType, a.Year);

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
            .Include(a => a.RegistrationId)
            .Include(a => a.RegistrationReference)
            .Include(a => a.OrganisationId)
            .Include(a => a.OrganisationName)
            .Include(a => a.MaterialType);

        var live = await collection
            .Find(Live, new FindOptions { MaxTime = ScanMaxTime })
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

    // One line per group, with its candidates and keepers, naming the group's organisation (id and
    // name) so a reviewer can tell what a dry run would delete. Never the reviewer, applicant
    // contact details or an exception message.
    private void LogGroup(RunMode mode, DuplicateGroup group)
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;

        logger.LogInformation(
            "RA-788 {Mode:l}: group organisationId={OrganisationId} organisationName={OrganisationName} registrationId={RegistrationId} "
                + "material={Material} year={Year} "
                + "outcome={Outcome:l} candidates={Candidates} [{CandidateDetail:l}] keepers={Keepers} [{KeeperDetail:l}]",
            TextOf(mode),
            group.Key.OrganisationId,
            group.Key.OrganisationName,
            group.Key.RegistrationId,
            group.Key.MaterialType,
            group.Key.Year,
            group.Outcome switch
            {
                GroupOutcome.Delete => mode == RunMode.Apply ? "delete" : "would-delete",
                GroupOutcome.NoKeeper => "skipped-no-keeper",
                _ => "skipped-no-candidate",
            },
            group.Candidates.Count,
            string.Join("; ", group.Candidates.Select(CandidateDetail)),
            group.Keepers.Count,
            string.Join("; ", group.Keepers.Select(KeeperDetail))
        );
    }

    private static string CandidateDetail(AccreditationApplicationModel c) =>
        Invariant(
            $"{c.Id}(registration={c.RegistrationReference} status={c.ApplicationStatus} version={c.Version} createdAt={c.CreatedAt:O} dateLastEdited={c.DateLastEdited:O})"
        );

    private static string KeeperDetail(AccreditationApplicationModel k) =>
        Invariant(
            $"{k.Id}(registration={k.RegistrationReference} status={k.ApplicationStatus} hasOrgId={k.OrgId is not null})"
        );

    // The first line of a run: the counts and the candidate ids that ExpectedApplicationIds is set from.
    private void LogSummary(RunMode mode, RunSummary s, IReadOnlyList<ObjectId> candidateIds)
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;
        logger.LogInformation(
            "RA-788 {Mode:l}: summary groups={Groups} candidates={Candidates} candidate-ids={CandidateIds:l} "
                + "skipped-no-keeper={NoKeeper} skipped-no-candidate={NoCandidate} "
                + "ungrouped-missing-key={Ungrouped}",
            TextOf(mode),
            s.Groups,
            s.Candidates,
            string.Join(",", candidateIds),
            s.SkippedNoKeeper,
            s.SkippedNoCandidate,
            s.UngroupedMissingKey
        );
    }

    private void LogDecision(ApplyDecision decision)
    {
        if (decision.Error is not null)
            logger.LogError("RA-788 {Mode:l}: {Error:l}", DryRunMode, decision.Error);
        if (decision.Note is not null)
            logger.LogInformation("RA-788 {Mode:l}: {Note:l}", ApplyMode, decision.Note);
    }

    // The last line of a run: what was (or would be) deleted.
    private void LogOutcome(RunMode mode, RunSummary s)
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;
        logger.LogInformation(
            "RA-788 {Mode:l}: outcome would-delete-records={WouldDelete} deleted-records={Deleted} "
                + "rolled-back-groups={RolledBack} outcome-unknown-groups={Unknown} "
                + "deleted-elsewhere-groups={Elsewhere}",
            TextOf(mode),
            s.WouldDeleteRecords,
            s.DeletedRecords,
            s.RolledBackGroups,
            s.OutcomeUnknownGroups,
            s.DeletedElsewhereGroups
        );
    }

    private static string Invariant(FormattableString text) =>
        text.ToString(CultureInfo.InvariantCulture);

    // ---- Delete ----

    private enum DeleteResult
    {
        Deleted,
        RolledBack,
        DeletedElsewhere,
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
            Filters.And(Filters.In(a => a.Id, keeperIds), Live),
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
        // A failed check always rolls back. If the candidates are all gone, though, another run
        // deleted them first (its delete here found nothing), which is not a failure.
        if (attempt.RollbackReason is { } reason)
        {
            if (await CountRemainingOrNullAsync(collection, group, cancellationToken) == 0)
            {
                logger.LogInformation(
                    "RA-788 {Mode:l}: candidates {Ids:l} were already deleted by another run; nothing more to do",
                    ApplyMode,
                    CandidateIds(group)
                );
                return DeleteResult.DeletedElsewhere;
            }
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

    // Null if the re-read fails: the caller then reports the plain rollback.
    private static async Task<long?> CountRemainingOrNullAsync(
        IMongoCollection<AccreditationApplicationModel> collection,
        DuplicateGroup group,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await collection.CountDocumentsAsync(
                Filters.In(a => a.Id, CandidateIdList(group).Cast<ObjectId?>()),
                cancellationToken: cancellationToken
            );
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
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

    // One line per group. The reviewer is not logged (see SpotCheckConfirmedBy).
    private void LogApplied(DuplicateGroup group)
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;
        logger.LogInformation(
            "RA-788 APPLIED: deleted applications {Ids:l} (versions {Versions:l}); group kept "
                + "{Keepers:l}. Spot check confirmed. Any domain-model copy of them is not deleted",
            CandidateIds(group),
            string.Join(
                ",",
                group.Candidates.Select(c => c.Version.ToString(CultureInfo.InvariantCulture))
            ),
            string.Join(",", group.Keepers.Select(k => k.Id.ToString()))
        );
    }

    // ---- Filters ----

    private static FilterDefinition<AccreditationApplicationModel> InGroup(GroupKey key) =>
        Filters.And(
            Filters.Eq(a => a.OrganisationId, key.OrganisationId),
            Filters.Eq(a => a.OrganisationName, key.OrganisationName),
            Filters.Eq(a => a.RegistrationId, key.RegistrationId),
            Filters.Eq(a => a.MaterialType, key.MaterialType),
            Filters.Eq(a => a.Year, key.Year)
        );

    private static FilterDefinition<AccreditationApplicationModel> LiveInGroup(GroupKey key) =>
        Filters.And(InGroup(key), Live);

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
