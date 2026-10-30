using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>
/// Ensure-migrated over a registration group (design doc §6.1 steps 1-6, §6.2). Registration and ORS are derived
/// from every application under the registration, so the whole group is migrated together, inside the caller's
/// transaction. It is idempotent: entities that already exist (from the seed, a write or an earlier run) are
/// reused, and only the missing ones are written. There is no lock: two first writes racing on the same group
/// collide on the current-version unique indexes, and the loser's transaction is retried by the caller and then
/// finds the group migrated.
/// </summary>
public sealed class ApplicationGroupMigrator(
    IAccreditationApplicationPersistence applications,
    ApplicationGroupStores stores,
    TimeProvider clock
) : IApplicationGroupMigrator
{
    /// <summary>The transaction limit for one group (§6.2). Counts legacy applications; the writes are a few per application.</summary>
    public const int MaxGroupSize = 100;

    public async Task<GroupMigrationResult> EnsureMigratedAsync(
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        var touched = await applications.GetByObjectIdAsync(
            applicationId,
            session,
            cancellationToken
        );
        if (touched is null)
        {
            return GroupMigrationResult.NotFound;
        }

        IReadOnlyList<AccreditationApplicationModel> group = touched.RegistrationId is null
            ? [touched]
            : await applications.GetRegistrationGroupAsync(
                touched.OrganisationId,
                touched.RegistrationId,
                MaxGroupSize + 1,
                session,
                cancellationToken
            );
        if (group.Count > MaxGroupSize)
        {
            throw new ApplicationGroupTooLargeException(touched.RegistrationId!, MaxGroupSize);
        }

        var ids = group.Select(a => a.Id!.Value).ToList();
        var migrated = (
            await stores.Applications.Versions.FindAllCurrentAsync(
                Builders<ApplicationEntity>.Filter.In(a => a.EntityId, ids),
                session,
                cancellationToken
            )
        )
            .Select(a => a.EntityId)
            .ToHashSet();
        var pending = group.Where(a => !migrated.Contains(a.Id!.Value)).ToList();
        if (pending.Count == 0)
        {
            return GroupMigrationResult.NothingToDo;
        }

        var run = new GroupRun(
            stores,
            applications,
            session,
            clock.GetUtcNow().UtcDateTime,
            Ranked(group),
            pending,
            cancellationToken
        );
        await run.ExecuteAsync();
        return new GroupMigrationResult(
            GroupMigrationOutcome.Migrated,
            pending.Count,
            run.Conflicts
        );
    }

    // The order "who wins" is decided in (§6.1 step 2): the newest approved application, otherwise the newest.
    private static List<AccreditationApplicationModel> Ranked(
        IEnumerable<AccreditationApplicationModel> group
    ) =>
        group
            .OrderByDescending(a => a.ApplicationStatus == ApplicationStatus.Approved)
            .ThenByDescending(a => a.UpdatedAt)
            .ThenByDescending(a => a.Id)
            .ToList();

    // An operator-added site's status follows its applications: accredited by an approved application that
    // selected it -> Active; otherwise held by one in flight -> Proposed; otherwise Discarded (§6.1 step 2, §6.3).
    private static OverseasSiteStatus StatusFor(IEnumerable<Holding> holders)
    {
        var list = holders.ToList();
        if (list.Exists(h => h.Accredits))
        {
            return OverseasSiteStatus.Active;
        }

        return list.Exists(h => h.Application.ApplicationStatus.IsInFlight())
            ? OverseasSiteStatus.Proposed
            : OverseasSiteStatus.Discarded;
    }

    // Who wins a site's details: an approved application that selected it, otherwise the newest. An approved
    // application that did not select a site did not accredit it, so it ranks like any other.
    private static List<Holding> RankedHoldings(IEnumerable<Holding> holdings) =>
        holdings
            .OrderByDescending(h => h.Accredits)
            .ThenByDescending(h => h.Application.UpdatedAt)
            .ThenByDescending(h => h.Application.Id)
            .ToList();

    /// <summary>One legacy site as one application holds it.</summary>
    private sealed record Holding(AccreditationApplicationModel Application, OverseasSiteModel Site)
    {
        public ObjectId ApplicationId => Application.Id!.Value;

        /// <summary>The application was approved with this site selected, so the site is accredited.</summary>
        public bool Accredits =>
            Application.ApplicationStatus == ApplicationStatus.Approved && Site.Selected;
    }

    /// <summary>One ORS: found, or to be created, with what it maps each legacy interim site number to.</summary>
    private sealed class SitePlan(List<Holding> holdings, OverseasSiteEntity? existing)
    {
        public List<Holding> Holdings { get; } = holdings;
        public OverseasSiteEntity? Existing { get; } = existing;
        public OverseasSiteEntity? Saved { get; set; }
        public ObjectId EntityId => (Saved ?? Existing)!.EntityId;
        public string? OrsId => Holdings[0].Site.OrsId ?? Existing?.OrsId;

        /// <summary>Legacy interim site number -> the number it has in the model.</summary>
        public Dictionary<string, string> InterimNumbers { get; } = [];

        public List<InterimSiteEntry> NewInterimSites { get; } = [];
        public bool Renumbered { get; set; }
    }

    /// <summary>The state of one migration of one group. Never shared between calls.</summary>
    private sealed class GroupRun(
        ApplicationGroupStores stores,
        IAccreditationApplicationPersistence applications,
        IClientSessionHandle session,
        DateTime now,
        List<AccreditationApplicationModel> ranked,
        List<AccreditationApplicationModel> pending,
        CancellationToken cancellationToken
    )
    {
        private readonly HashSet<ObjectId> _pendingIds = pending
            .Select(a => a.Id!.Value)
            .ToHashSet();
        private readonly Dictionary<string, SitePlan> _sitesByOrsId = [];
        private readonly Dictionary<(ObjectId, int), SitePlan> _sitesWithoutOrsId = [];
        private readonly Dictionary<(ObjectId, int), BsonDocument> _proposals = [];
        private readonly Dictionary<ObjectId, ObjectId> _accreditations = [];
        private RegistrationEntity? _registration;

        public List<MigrationConflict> Conflicts { get; } = [];

        private AccreditationApplicationModel Newest => ranked[0];

        public async Task ExecuteAsync()
        {
            var organisationId = await stores.Organisations.GetOrCreateAsync(
                Newest.OrganisationId,
                NewOrganisation,
                session,
                cancellationToken
            );
            if (Newest.RegistrationId is not null)
            {
                _registration = await EnsureRegistrationAsync(
                    organisationId,
                    Newest.RegistrationId
                );
                await EnsureSitesAsync(_registration.EntityId);
                await EnsureAccreditationsAsync(_registration.EntityId);
                RecordDuplicateLiveApplications();
            }

            foreach (var application in pending)
            {
                await InsertApplicationVersionAsync(application);
                await StampAsync(application);
            }

            await SaveConflictsAsync();
        }

        // --- step 1: Organisation and Registration -------------------------------------------------------------------

        private OrganisationEntity NewOrganisation() =>
            new()
            {
                ReExOrganisationId = Newest.OrganisationId,
                Name = Newest.OrganisationName,
                OrganisationNumber = Newest.OrgId,
                CompaniesHouseNumber = Newest.CompaniesHouseNumber,
                Source = new EntitySource { EnrichmentPending = true, ImportedAt = now },
                UpdatedAt = now,
            };

        private async Task<RegistrationEntity> EnsureRegistrationAsync(
            ObjectId organisationId,
            string reExRegistrationId
        )
        {
            var versions = stores.Registrations.Versions;
            var current = await versions.FindCurrentAsync(
                Builders<RegistrationEntity>.Filter.Eq(
                    x => x.ReExRegistrationId,
                    reExRegistrationId
                ),
                session,
                cancellationToken
            );
            var winner = ranked.Find(a => !string.IsNullOrWhiteSpace(a.RegistrationReference));

            RegistrationEntity registration;
            if (current is null)
            {
                registration = await versions.InsertAsync(
                    NewRegistration(organisationId, reExRegistrationId, winner),
                    Change(),
                    session,
                    cancellationToken
                );
            }
            else if (current.RegistrationNumber is null && winner is not null)
            {
                // Keep everything the seed copied from ReEx; only fill in the number it did not have.
                current.RegistrationNumber = winner.RegistrationReference;
                registration = (
                    await versions.UpdateAsync(
                        current,
                        current.Version,
                        Change(),
                        session,
                        cancellationToken
                    )
                ).Entity;
            }
            else
            {
                registration = current;
            }

            RecordRegistrationNumberConflicts(registration, winner);
            return registration;
        }

        private RegistrationEntity NewRegistration(
            ObjectId organisationId,
            string reExRegistrationId,
            AccreditationApplicationModel? numberWinner
        ) =>
            new()
            {
                OrganisationId = organisationId,
                ReExRegistrationId = reExRegistrationId,
                RegistrationNumber = numberWinner?.RegistrationReference,
                PreviousRegistrationNumbers = ranked
                    .SelectMany(a => a.PreviousRegistrationNumbers)
                    .Distinct()
                    .ToList(),
                Material = Newest.MaterialType,
                Nation = Newest.Nation,
                GlassRecyclingProcess = Newest.GlassRecyclingProcess,
                WasteProcessingType = Newest.IsExporter
                    ? WasteProcessingKind.Exporter
                    : WasteProcessingKind.Reprocessor,
                Source = new EntitySource
                {
                    System = SourceSystem.Application,
                    EnrichmentPending = true,
                    ImportedAt = now,
                },
            };

        private void RecordRegistrationNumberConflicts(
            RegistrationEntity registration,
            AccreditationApplicationModel? winner
        )
        {
            var losers = pending
                .Where(a =>
                    !string.IsNullOrWhiteSpace(a.RegistrationReference)
                    && a.RegistrationReference != registration.RegistrationNumber
                )
                .Select(a => a.Id!.Value)
                .ToList();
            if (losers.Count > 0)
            {
                Conflicts.Add(
                    Conflict(MigrationConflictKind.RegistrationNumberDiffers, losers, winner?.Id)
                );
            }
        }

        // --- steps 2 and 3: ORSs and their interim sites ---------------------------------------------------------------

        private async Task EnsureSitesAsync(ObjectId registrationId)
        {
            var existing = await stores.OverseasSites.Versions.FindAllCurrentAsync(
                Builders<OverseasSiteEntity>.Filter.Eq(x => x.RegistrationId, registrationId),
                session,
                cancellationToken
            );
            var existingByOrsId = existing
                .Where(s => s.OrsId is not null)
                .ToDictionary(s => s.OrsId!);

            var holdings = ranked
                .SelectMany(a => (a.OverseasSites?.Sites ?? []).Select(s => new Holding(a, s)))
                .ToList();
            foreach (
                var byOrsId in holdings
                    .Where(h => h.Site.OrsId is not null)
                    .GroupBy(h => h.Site.OrsId!)
            )
            {
                _sitesByOrsId[byOrsId.Key] = new SitePlan(
                    RankedHoldings(byOrsId),
                    existingByOrsId.GetValueOrDefault(byOrsId.Key)
                );
            }

            // A legacy site with no orsId has nothing to match it across applications: it is its own ORS, and only
            // the application being migrated now creates it, so a re-run never creates it twice.
            foreach (
                var holding in holdings.Where(h =>
                    h.Site.OrsId is null && _pendingIds.Contains(h.ApplicationId)
                )
            )
            {
                _sitesWithoutOrsId[(holding.ApplicationId, holding.Site.SiteId)] = new SitePlan(
                    [holding],
                    null
                );
                Conflicts.Add(
                    Conflict(MigrationConflictKind.OrsIdMissing, [holding.ApplicationId])
                );
            }

            var plans = _sitesByOrsId.Values.Concat(_sitesWithoutOrsId.Values).ToList();
            PlanInterimSiteNumbers(plans, existing);
            foreach (var plan in plans)
            {
                await SaveSiteAsync(registrationId, plan);
                RecordSiteDifferences(plan);
            }
        }

        // Interim site numbers are unique across the registration's ORSs (RA-603), so they are allocated for the whole
        // group at once: every number already in the model is taken, then every legacy number that is three digits
        // and still free is kept, and only then are the rest (SN-000N, or a number another ORS already has)
        // renumbered into the pool, so a renumbering never takes a number a later site was going to keep.
        private void PlanInterimSiteNumbers(List<SitePlan> plans, List<OverseasSiteEntity> existing)
        {
            var used = existing
                .SelectMany(s => s.InterimSites)
                .SelectMany(i => i.PreviousSiteNumbers.Prepend(i.SiteNumber))
                .ToHashSet();
            var toNumber = new List<(SitePlan Plan, string Legacy)>();

            foreach (var plan in plans)
            {
                foreach (var legacy in InterimHoldings(plan).Select(g => g.Key))
                {
                    var stored = LegacySiteMapper.FindInterim(plan.Existing, legacy);
                    if (stored is not null)
                    {
                        plan.InterimNumbers[legacy] = stored.SiteNumber;
                    }
                    else if (LegacySiteMapper.IsThreeDigitNumber(legacy) && used.Add(legacy))
                    {
                        plan.InterimNumbers[legacy] = legacy;
                    }
                    else
                    {
                        toNumber.Add((plan, legacy));
                    }
                }
            }

            foreach (var (plan, legacy) in toNumber)
            {
                var next =
                    OrsIdGenerator.GenerateNext(used).OrsId
                    ?? throw new InvalidOperationException(
                        "The registration has no interim site numbers left to renumber into."
                    );
                used.Add(next);
                plan.InterimNumbers[legacy] = next;
                plan.Renumbered = true;
                var holders = InterimHoldings(plan)
                    .Single(g => g.Key == legacy)
                    .Select(h => h.Holding.ApplicationId)
                    .Distinct()
                    .ToList();
                Conflicts.Add(
                    Conflict(
                        MigrationConflictKind.InterimSiteRenumbered,
                        holders,
                        orsId: plan.OrsId,
                        siteNumber: legacy
                    )
                );
            }

            foreach (var plan in plans)
            {
                plan.NewInterimSites.AddRange(NewInterimSites(plan));
            }
        }

        private static IEnumerable<
            IGrouping<string, (Holding Holding, InterimSiteModel Interim)>
        > InterimHoldings(SitePlan plan) =>
            plan
                .Holdings.SelectMany(h =>
                    InterimSiteSync.All(h.Site).Select(i => (Holding: h, Interim: i))
                )
                .GroupBy(x => x.Interim.SiteNumber);

        private IEnumerable<InterimSiteEntry> NewInterimSites(SitePlan plan)
        {
            foreach (var byNumber in InterimHoldings(plan))
            {
                var number = plan.InterimNumbers[byNumber.Key];
                if (plan.Existing?.InterimSites.Exists(i => i.SiteNumber == number) == true)
                {
                    continue;
                }

                var (winner, interim) = byNumber.First();
                var entry = LegacySiteMapper.ToInterimSite(interim, number);
                entry.PreviousSiteNumbers = number == byNumber.Key ? [] : [byNumber.Key];
                entry.Status = InterimStatus(interim, byNumber.Select(x => x.Holding));
                entry.ProposedByApplicationId = interim.IsNewSite ? winner.ApplicationId : null;
                RecordInterimDifferences(plan, byNumber.ToList(), entry);
                yield return entry;
            }
        }

        private static InterimSiteStatus InterimStatus(
            InterimSiteModel interim,
            IEnumerable<Holding> holders
        ) =>
            LegacySiteMapper.InterimStatus(
                interim,
                StatusFor(holders) switch
                {
                    OverseasSiteStatus.Active => InterimSiteStatus.Active,
                    OverseasSiteStatus.Proposed => InterimSiteStatus.Proposed,
                    _ => InterimSiteStatus.Discarded,
                }
            );

        private void RecordInterimDifferences(
            SitePlan plan,
            List<(Holding Holding, InterimSiteModel Interim)> holders,
            InterimSiteEntry kept
        )
        {
            var winner = holders[0].Holding.ApplicationId;
            var keptDetails = LegacySiteMapper
                .ToInterimSite(holders[0].Interim, kept.SiteNumber)
                .ToBsonDocument();
            var losers = holders
                .Skip(1)
                .Where(h => _pendingIds.Contains(h.Holding.ApplicationId))
                .Where(h =>
                    LegacySiteMapper.ToInterimSite(h.Interim, kept.SiteNumber).ToBsonDocument()
                    != keptDetails
                )
                .Select(h => h.Holding.ApplicationId)
                .Distinct()
                .ToList();
            if (losers.Count > 0)
            {
                Conflicts.Add(
                    Conflict(
                        MigrationConflictKind.InterimSiteDetailsDiffer,
                        losers,
                        winner,
                        plan.OrsId,
                        kept.SiteNumber
                    )
                );
            }
        }

        private async Task SaveSiteAsync(ObjectId registrationId, SitePlan plan)
        {
            var versions = stores.OverseasSites.Versions;
            var change = Change(
                plan.Renumbered ? ChangeReason.LegacySiteNumberRenumbered : ChangeReason.Migration,
                plan.OrsId
            );
            if (plan.Existing is { } existing)
            {
                if (plan.NewInterimSites.Count > 0)
                {
                    existing.InterimSites.AddRange(plan.NewInterimSites);
                    plan.Saved = (
                        await versions.UpdateAsync(
                            existing,
                            existing.Version,
                            change,
                            session,
                            cancellationToken
                        )
                    ).Entity;
                }

                return;
            }

            var winner = plan.Holdings[0];
            var site = LegacySiteMapper.NewOverseasSite(registrationId, winner.Site);
            site.Origin = plan.Holdings.Exists(h => !h.Site.IsNewSite)
                ? SiteOrigin.ReEx
                : SiteOrigin.Operator;
            site.Status =
                site.Origin == SiteOrigin.ReEx
                    ? OverseasSiteStatus.Active
                    : StatusFor(plan.Holdings);
            site.ProposedByApplicationId =
                site.Origin == SiteOrigin.Operator ? winner.ApplicationId : null;
            site.InterimSites = plan.NewInterimSites;
            plan.Saved = await versions.InsertAsync(site, change, session, cancellationToken);
        }

        // Where an application's copy of a site differs from the ORS: an in-flight application keeps its version as
        // proposedChanges; an application that cannot change any more has lost it, which is a conflict. A site this
        // run created was built from the winner, so differing applications are its losers (§6.1 step 2).
        private void RecordSiteDifferences(SitePlan plan)
        {
            var ors = plan.Saved ?? plan.Existing!;
            var created = plan.Existing is null;
            var losers = new List<ObjectId>();
            foreach (var holding in plan.Holdings.Where(h => _pendingIds.Contains(h.ApplicationId)))
            {
                var changes = LegacySiteMapper.ProposedChanges(ors, holding.Site);
                if (changes is null)
                {
                    continue;
                }

                if (holding.Application.ApplicationStatus.IsInFlight())
                {
                    _proposals[(holding.ApplicationId, holding.Site.SiteId)] = changes;
                }

                if (created || !holding.Application.ApplicationStatus.IsInFlight())
                {
                    losers.Add(holding.ApplicationId);
                }
            }

            if (losers.Count > 0)
            {
                Conflicts.Add(
                    Conflict(
                        MigrationConflictKind.OrsDetailsDiffer,
                        losers.Distinct().ToList(),
                        created ? plan.Holdings[0].ApplicationId : null,
                        plan.OrsId
                    )
                );
            }
        }

        private SitePlan? PlanFor(
            AccreditationApplicationModel application,
            OverseasSiteModel site
        ) =>
            site.OrsId is null
                ? _sitesWithoutOrsId.GetValueOrDefault((application.Id!.Value, site.SiteId))
                : _sitesByOrsId.GetValueOrDefault(site.OrsId);

        // --- step 4: Application versions ---------------------------------------------------------------------------

        private async Task InsertApplicationVersionAsync(AccreditationApplicationModel application)
        {
            var entries = (application.OverseasSites?.Sites ?? [])
                .Select(site => SiteEntry(application, site))
                .ToList();
            var entity = LegacyApplicationMapper.ToEntity(
                application,
                _registration?.EntityId,
                entries
            );
            entity.AccreditationId = _accreditations.TryGetValue(entity.EntityId, out var id)
                ? id
                : null;
            await stores.Applications.Versions.InsertAsync(
                entity,
                new ChangeInfo { Reason = ChangeReason.Migration, ApplicationId = entity.EntityId },
                session,
                cancellationToken
            );
        }

        private ApplicationOverseasSiteEntry SiteEntry(
            AccreditationApplicationModel application,
            OverseasSiteModel site
        )
        {
            var plan = PlanFor(application, site);
            return LegacyApplicationMapper.ToSiteEntry(
                site,
                plan?.EntityId,
                _proposals.GetValueOrDefault((application.Id!.Value, site.SiteId)),
                number => plan?.InterimNumbers.GetValueOrDefault(number) ?? number
            );
        }

        // --- step 5: Accreditations from approved applications -------------------------------------------------------

        private async Task EnsureAccreditationsAsync(ObjectId registrationId)
        {
            foreach (
                var application in pending.Where(a =>
                    a.ApplicationStatus == ApplicationStatus.Approved
                )
            )
            {
                var accreditationId = await EnsureAccreditationAsync(registrationId, application);
                if (accreditationId is { } id)
                {
                    _accreditations[application.Id!.Value] = id;
                }
            }
        }

        private async Task<ObjectId?> EnsureAccreditationAsync(
            ObjectId registrationId,
            AccreditationApplicationModel application
        )
        {
            var found = await AccreditationFromApplication.FindAsync(
                stores.Accreditations,
                registrationId,
                application,
                session,
                cancellationToken
            );
            if (found.Own is { } own)
            {
                return own.EntityId;
            }

            if (found.SameYear is not null)
            {
                Conflicts.Add(
                    Conflict(
                        MigrationConflictKind.AccreditationAlreadyExists,
                        [application.Id!.Value]
                    )
                );
                return null;
            }

            if (found.NumberMissing)
            {
                Conflicts.Add(
                    Conflict(
                        MigrationConflictKind.AccreditationNumberMissing,
                        [application.Id!.Value]
                    )
                );
            }

            var accreditation = await AccreditationFromApplication.InsertAsync(
                stores.Accreditations,
                registrationId,
                application,
                site => PlanFor(application, site)?.EntityId,
                ChangeReason.Migration,
                session,
                cancellationToken
            );
            return accreditation.EntityId;
        }

        // --- conflicts that span applications -----------------------------------------------------------------------

        // A defect (one live application per key), which the Seed check is only best-effort against: both are linked
        // to the same Registration and listed, never merged (§6.3).
        private void RecordDuplicateLiveApplications()
        {
            var duplicates = ranked
                .Where(a =>
                    a.ApplicationStatus
                        is not (ApplicationStatus.Rejected or ApplicationStatus.Withdrawn)
                )
                .GroupBy(a => (a.MaterialType, a.Year))
                .Where(g => g.Count() > 1 && g.Any(a => _pendingIds.Contains(a.Id!.Value)));
            foreach (var group in duplicates)
            {
                Conflicts.Add(
                    Conflict(
                        MigrationConflictKind.DuplicateLiveApplications,
                        group.Select(a => a.Id!.Value).ToList()
                    )
                );
            }
        }

        // --- step 6: stamp the legacy application -------------------------------------------------------------------

        private async Task StampAsync(AccreditationApplicationModel application)
        {
            if (_registration is null)
            {
                return;
            }

            var siteIds = (application.OverseasSites?.Sites ?? [])
                .Where(s => s.OrsId is not null)
                .Select(s => (OrsId: s.OrsId!, Plan: PlanFor(application, s)))
                .Where(x => x.Plan is not null)
                .DistinctBy(x => x.OrsId)
                .ToDictionary(x => x.OrsId, x => x.Plan!.EntityId);
            await applications.StampDomainModelForeignKeysAsync(
                application.Id!.Value,
                _registration.EntityId,
                siteIds,
                session,
                cancellationToken,
                _accreditations.TryGetValue(application.Id!.Value, out var id) ? id : null
            );
        }

        private async Task SaveConflictsAsync()
        {
            foreach (var conflict in Conflicts)
            {
                conflict.Id = ObjectId.GenerateNewId();
                conflict.RegistrationId = _registration?.EntityId;
                conflict.RecordedAt = now;
            }

            await stores.Conflicts.InsertManyAsync(Conflicts, session, cancellationToken);
        }

        private MigrationConflict Conflict(
            MigrationConflictKind kind,
            List<ObjectId> applicationIds,
            ObjectId? winner = null,
            string? orsId = null,
            string? siteNumber = null
        ) =>
            new()
            {
                Kind = kind,
                ReExRegistrationId = Newest.RegistrationId ?? "",
                ApplicationIds = applicationIds,
                WinnerApplicationId = winner,
                OrsId = orsId,
                SiteNumber = siteNumber,
            };

        private static ChangeInfo Change(
            ChangeReason reason = ChangeReason.Migration,
            string? orsId = null
        ) => ChangeInfo.Of(reason, orsId: orsId);
    }
}
