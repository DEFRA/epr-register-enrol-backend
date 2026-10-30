using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Status;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary>
/// Re-projects the legacy application onto the model, so no write path maps fields itself. Sites follow option B
/// (design doc §3.2): a new site is a Proposed ORS at once; an edit to a site this application proposed writes
/// through to the ORS; an edit to any other site is held in the entry's <c>proposedChanges</c>; a site this
/// application proposed and then removed is Discarded. Every write is a versioned update, which is a no-op when
/// nothing changed, so running it after a write that changed nothing writes nothing.
/// </summary>
/// <remarks>
/// It holds no state across calls: the transaction can re-run it, so it reads everything it changes in the session.
/// </remarks>
public sealed class ApplicationModelSync(
    IAccreditationApplicationPersistence applications,
    ApplicationGroupStores stores,
    TimeProvider clock,
    ILogger<ApplicationModelSync> logger
) : IApplicationModelSync
{
    public async Task SyncAsync(
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        var legacy = await applications.GetByObjectIdAsync(
            applicationId,
            session,
            cancellationToken
        );
        if (legacy is null)
        {
            return;
        }

        var current = await stores.Applications.Versions.FindCurrentAsync(
            Builders<ApplicationEntity>.Filter.Eq(a => a.EntityId, applicationId),
            session,
            cancellationToken
        );
        if (current is null)
        {
            return;
        }

        var run = new SyncRun(
            stores,
            applications,
            new SyncDiagnostics(clock, logger),
            session,
            legacy,
            current,
            cancellationToken
        );
        var entries = await run.SyncSitesAsync();

        var next = LegacyApplicationMapper.ToEntity(legacy, current.RegistrationId, entries);
        next.AccreditationId =
            await run.EnsureAccreditationAsync(entries) ?? current.AccreditationId;
        await stores.Applications.Versions.UpdateAsync(
            next,
            current.Version,
            new ChangeInfo
            {
                Reason = ChangeReason.ApplicationEdited,
                ApplicationId = applicationId,
            },
            session,
            cancellationToken
        );
        await IssueRegistrationNumberAsync(
            current.RegistrationId,
            legacy.RegistrationReference,
            applicationId,
            session,
            cancellationToken
        );
    }

    // B8e: a Registration takes the application's number only while it has none. An issued number is never
    // changed by a write path, so an application holding a different (older) number cannot roll it back.
    private async Task IssueRegistrationNumberAsync(
        ObjectId? registrationId,
        string? number,
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken
    )
    {
        if (registrationId is not { } id || string.IsNullOrWhiteSpace(number))
        {
            return;
        }

        var registration = await stores.Registrations.Versions.FindCurrentAsync(
            Builders<RegistrationEntity>.Filter.Eq(r => r.EntityId, id),
            session,
            cancellationToken
        );
        if (registration is null || !string.IsNullOrWhiteSpace(registration.RegistrationNumber))
        {
            return;
        }

        registration.RegistrationNumber = number;
        await stores.Registrations.Versions.UpdateAsync(
            registration,
            registration.Version,
            new ChangeInfo
            {
                Reason = ChangeReason.RegistrationNumberIssued,
                ApplicationId = applicationId,
            },
            session,
            cancellationToken
        );
    }

    // What a sync needs only to record what it could not settle.
    private sealed record SyncDiagnostics(TimeProvider Clock, ILogger<ApplicationModelSync> Logger);

    /// <summary>One sync of one application. Never shared between calls.</summary>
    private sealed class SyncRun(
        ApplicationGroupStores stores,
        IAccreditationApplicationPersistence applications,
        SyncDiagnostics diagnostics,
        IClientSessionHandle session,
        AccreditationApplicationModel legacy,
        ApplicationEntity current,
        CancellationToken cancellationToken
    )
    {
        private readonly ObjectId _applicationId = current.EntityId;
        private readonly Dictionary<string, OverseasSiteEntity> _byOrsId = [];
        private readonly Dictionary<ObjectId, OverseasSiteEntity> _byEntityId = [];
        private readonly HashSet<(ObjectId OrsId, string SiteNumber)> _heldInterims = [];

        private List<OverseasSiteModel> Sites => legacy.OverseasSites?.Sites ?? [];

        public async Task<List<ApplicationOverseasSiteEntry>> SyncSitesAsync()
        {
            if (current.RegistrationId is not { } registrationId)
            {
                return Sites.Select(site => Entry(site, null, null)).ToList();
            }

            var existing = await stores.OverseasSites.Versions.FindAllCurrentAsync(
                Builders<OverseasSiteEntity>.Filter.Eq(s => s.RegistrationId, registrationId),
                session,
                cancellationToken
            );
            foreach (var ors in existing)
            {
                Track(ors);
            }

            var previous = current
                .ApplicationOverseasSites.GroupBy(e => e.SiteId)
                .ToDictionary(g => g.Key, g => g.First().OverseasSiteId);
            if (legacy.ApplicationStatus == ApplicationStatus.Approved)
            {
                await ApproveSitesAsync(previous);
            }
            var entries = new List<ApplicationOverseasSiteEntry>();
            var stamps = new Dictionary<string, ObjectId>();
            foreach (var site in Sites)
            {
                var (ors, changes) = await SyncSiteAsync(
                    registrationId,
                    site,
                    previous.GetValueOrDefault(site.SiteId)
                );
                entries.Add(Entry(site, ors, changes));
                if (
                    site.OrsId is not null
                    && ors is not null
                    && site.OverseasSiteEntityId != ors.EntityId
                )
                {
                    stamps.TryAdd(site.OrsId, ors.EntityId);
                }
            }

            await DiscardDroppedInterimProposalsAsync(entries);
            await DiscardRemovedProposalsAsync(entries);
            if (
                legacy.ApplicationStatus
                is ApplicationStatus.Rejected
                    or ApplicationStatus.Withdrawn
            )
            {
                await DiscardClosedApplicationsProposalsAsync(registrationId, entries);
            }

            if (stamps.Count > 0)
            {
                await applications.StampDomainModelForeignKeysAsync(
                    _applicationId,
                    registrationId,
                    stamps,
                    session,
                    cancellationToken
                );
            }

            return entries;
        }

        // One legacy site: its ORS (created, written through, or left alone with the edit as an overlay), then its
        // interim sites. Returns the ORS as it now stands and the overlay, if any.
        private async Task<(OverseasSiteEntity? Ors, BsonDocument? Changes)> SyncSiteAsync(
            ObjectId registrationId,
            OverseasSiteModel site,
            ObjectId? previousOverseasSiteId
        )
        {
            var ors = Find(site, previousOverseasSiteId);
            BsonDocument? changes = null;
            if (ors is null && site.OrsId is null)
            {
                await RecordOrsIdMissingAsync(registrationId, site);
            }
            else if (ors is null)
            {
                ors = await CreateAsync(registrationId, site);
            }
            else if (IsOwnProposal(ors))
            {
                ors = await WriteThroughAsync(ors, site);
            }
            else
            {
                changes = LegacySiteMapper.ProposedChanges(ors, site);
            }

            if (ors is not null)
            {
                ors = await SyncInterimSitesAsync(ors, site);
            }

            return (ors, changes);
        }

        // Live writes carry a backend-allocated orsId (RA-482), so a site without one is old legacy data. It stays
        // unlinked, as the sync cannot invent an id (R3), and is recorded once per application for the
        // reconciliation report, as the migration records it. Ids only in the log and the conflict.
        private async Task RecordOrsIdMissingAsync(ObjectId registrationId, OverseasSiteModel site)
        {
            if (
                await stores.Conflicts.ExistsAsync(
                    MigrationConflictKind.OrsIdMissing,
                    _applicationId,
                    session,
                    cancellationToken
                )
            )
            {
                return;
            }

            diagnostics.Logger.LogWarning(
                "Dual-write sync found an overseas site without an orsId: application {ApplicationId}, site {SiteId}",
                _applicationId,
                site.SiteId
            );
            await stores.Conflicts.InsertManyAsync(
                [
                    new MigrationConflict
                    {
                        Id = ObjectId.GenerateNewId(),
                        Kind = MigrationConflictKind.OrsIdMissing,
                        ReExRegistrationId = legacy.RegistrationId ?? "",
                        RegistrationId = registrationId,
                        ApplicationIds = [_applicationId],
                        RecordedAt = diagnostics.Clock.GetUtcNow().UtcDateTime,
                    },
                ],
                session,
                cancellationToken
            );
        }

        private void Track(OverseasSiteEntity ors)
        {
            _byEntityId[ors.EntityId] = ors;
            if (ors.OrsId is not null)
            {
                _byOrsId[ors.OrsId] = ors;
            }
        }

        // A site with no orsId (legacy data only) is found through the ORS its entry already points at.
        private OverseasSiteEntity? Find(OverseasSiteModel site, ObjectId? previousOverseasSiteId)
        {
            if (site.OrsId is not null)
                return _byOrsId.GetValueOrDefault(site.OrsId);
            if (previousOverseasSiteId is { } id)
                return _byEntityId.GetValueOrDefault(id);
            return null;
        }

        // Only the application that proposed a site may change it; any other holder overlays (option B).
        private bool IsOwnProposal(OverseasSiteEntity ors) =>
            ors.Status == OverseasSiteStatus.Proposed
            && ors.ProposedByApplicationId == _applicationId;

        // A site the application added is Proposed; one it already held from ReEx but the model lacks is Active, as
        // the migration would have made it.
        private async Task<OverseasSiteEntity> CreateAsync(
            ObjectId registrationId,
            OverseasSiteModel site
        )
        {
            var ors = LegacySiteMapper.NewOverseasSite(registrationId, site);
            ors.Origin = site.IsNewSite ? SiteOrigin.Operator : SiteOrigin.ReEx;
            ors.Status = site.IsNewSite ? OverseasSiteStatus.Proposed : OverseasSiteStatus.Active;
            ors.ProposedByApplicationId = site.IsNewSite ? _applicationId : null;
            ReconcileInterimSites(ors, site);
            var created = await stores.OverseasSites.Versions.InsertAsync(
                ors,
                Change(
                    site.IsNewSite ? ChangeReason.SiteProposed : ChangeReason.Migration,
                    ors.OrsId
                ),
                session,
                cancellationToken
            );
            Track(created);
            return created;
        }

        private async Task<OverseasSiteEntity> WriteThroughAsync(
            OverseasSiteEntity ors,
            OverseasSiteModel site
        )
        {
            var written = (
                await stores.OverseasSites.Versions.UpdateAsync(
                    LegacySiteMapper.WithDetailsOf(ors, site),
                    ors.Version,
                    Change(ChangeReason.ProposedSiteEdited, ors.OrsId),
                    session,
                    cancellationToken
                )
            ).Entity;
            Track(written);
            return written;
        }

        private async Task DiscardRemovedProposalsAsync(List<ApplicationOverseasSiteEntry> entries)
        {
            var held = entries.Select(e => e.OverseasSiteId).OfType<ObjectId>().ToHashSet();
            foreach (
                var ors in _byEntityId
                    .Values.Where(IsOwnProposal)
                    .Where(s => !held.Contains(s.EntityId))
                    .ToList()
            )
            {
                OverseasSiteTransitions.Registered.EnsureCanTransition(
                    ors.Status,
                    OverseasSiteStatus.Discarded
                );
                ors.Status = OverseasSiteStatus.Discarded;
                await stores.OverseasSites.Versions.UpdateAsync(
                    ors,
                    ors.Version,
                    Change(ChangeReason.ProposedSiteDiscarded, ors.OrsId),
                    session,
                    cancellationToken
                );
            }
        }

        // Interim sites follow option B inside their ORS: one the ORS lacks is added (Proposed and owned by this
        // application when it is new), and an edit to one this application proposed is written through. Edits to
        // any other interim site are left to the entry's overlay (see Entry). Every change is one ORS version.
        private async Task<OverseasSiteEntity> SyncInterimSitesAsync(
            OverseasSiteEntity ors,
            OverseasSiteModel site
        )
        {
            var change = ReconcileInterimSites(ors, site);
            if (change is not { } interimChange)
            {
                return ors;
            }

            var written = (
                await stores.OverseasSites.Versions.UpdateAsync(
                    ors,
                    ors.Version,
                    Change(interimChange.Reason, ors.OrsId, interimChange.SiteNumber),
                    session,
                    cancellationToken
                )
            ).Entity;
            Track(written);
            return written;
        }

        // Changes the ORS's interim sites in place and returns the first change made, for the version's reason.
        private (ChangeReason Reason, string SiteNumber)? ReconcileInterimSites(
            OverseasSiteEntity ors,
            OverseasSiteModel site
        )
        {
            (ChangeReason Reason, string SiteNumber)? first = null;
            foreach (var interim in InterimSiteSync.All(site))
            {
                var stored = LegacySiteMapper.FindInterim(ors, interim.SiteNumber);
                ChangeReason? reason = null;
                if (stored is null)
                {
                    stored = NewInterimSite(interim);
                    ors.InterimSites.Add(stored);
                    reason = ChangeReason.InterimSiteAdded;
                }
                else if (IsOwnInterimProposal(stored))
                {
                    reason = WriteInterimThrough(stored, interim);
                }

                _heldInterims.Add((ors.EntityId, stored.SiteNumber));
                if (reason is { } changed)
                {
                    first ??= (changed, stored.SiteNumber);
                }
            }

            if (ors.DuplicateInterimSiteNumbers() is [var duplicate, ..])
            {
                throw new InvalidOperationException(
                    $"Interim site number {duplicate} appears more than once on ORS {ors.EntityId}."
                );
            }

            return first;
        }

        private InterimSiteEntry NewInterimSite(InterimSiteModel interim)
        {
            var entry = LegacySiteMapper.ToInterimSite(interim, interim.SiteNumber);
            entry.Status = InterimStatus(interim);
            entry.ProposedByApplicationId = interim.IsNewSite ? _applicationId : null;
            return entry;
        }

        // A withdrawn interim site is Withdrawn whoever added it; otherwise one this application added is Proposed,
        // and one it already held is Active, as the migration would have made it.
        private static InterimSiteStatus InterimStatus(InterimSiteModel interim)
        {
            if (interim.RemovedAt is not null)
                return InterimSiteStatus.Withdrawn;
            return interim.IsNewSite ? InterimSiteStatus.Proposed : InterimSiteStatus.Active;
        }

        // Copies the application's details over its own proposal; null when nothing differed.
        private static ChangeReason? WriteInterimThrough(
            InterimSiteEntry stored,
            InterimSiteModel interim
        )
        {
            if (LegacySiteMapper.InterimProposedChanges(stored, interim) is null)
                return null;

            var reason = WithdrawalChange(stored.RemovedAt, interim.RemovedAt);
            CopyInterimDetails(stored, interim);
            stored.Status = stored.RemovedAt is null
                ? InterimSiteStatus.Proposed
                : InterimSiteStatus.Withdrawn;
            return reason;
        }

        private static void CopyInterimDetails(InterimSiteEntry stored, InterimSiteModel interim)
        {
            var details = LegacySiteMapper.ToInterimSite(interim, stored.SiteNumber);
            stored.SiteName = details.SiteName;
            stored.Address = details.Address;
            stored.Contact = details.Contact;
            stored.OperationCodes = details.OperationCodes;
            stored.RemovedAt = details.RemovedAt;
            stored.CreatedAt ??= details.CreatedAt;
        }

        // B9: an approved application's sites take its details and become Active, with their interim sites, as the
        // migration leaves them (the newest approved application wins, §6.1 step 2). One ORS version each; a
        // versioned update is a no-op when nothing changed, so a repeated approval writes nothing.
        private async Task ApproveSitesAsync(Dictionary<int, ObjectId?> previous)
        {
            foreach (var site in Sites)
            {
                if (Find(site, previous.GetValueOrDefault(site.SiteId)) is not { } ors)
                {
                    continue;
                }

                var approved = LegacySiteMapper.WithDetailsOf(ors, site);
                if (approved.Status != OverseasSiteStatus.Active)
                {
                    OverseasSiteTransitions.Registered.EnsureCanTransition(
                        approved.Status,
                        OverseasSiteStatus.Active
                    );
                    approved.Status = OverseasSiteStatus.Active;
                }

                ApproveInterimSites(approved, site);
                Track(
                    (
                        await stores.OverseasSites.Versions.UpdateAsync(
                            approved,
                            ors.Version,
                            Change(ChangeReason.ApplicationApproved, ors.OrsId),
                            session,
                            cancellationToken
                        )
                    ).Entity
                );
            }
        }

        // A withdrawn interim site stays Withdrawn; every other one the application holds is Active.
        private void ApproveInterimSites(OverseasSiteEntity ors, OverseasSiteModel site)
        {
            foreach (var interim in InterimSiteSync.All(site))
            {
                var stored = LegacySiteMapper.FindInterim(ors, interim.SiteNumber);
                if (stored is null)
                {
                    stored = NewInterimSite(interim);
                    ors.InterimSites.Add(stored);
                }

                CopyInterimDetails(stored, interim);
                stored.Status = stored.RemovedAt is null
                    ? InterimSiteStatus.Active
                    : InterimSiteStatus.Withdrawn;
            }
        }

        // B9: an approved application has one Accreditation (unique on applicationId, so a repeated approval finds
        // it). A missing number, or another Accreditation for the registration and year, fails the approval: the
        // migration only records those as conflicts, but a live approval must never store one (D9).
        public async Task<ObjectId?> EnsureAccreditationAsync(
            List<ApplicationOverseasSiteEntry> entries
        )
        {
            if (
                legacy.ApplicationStatus != ApplicationStatus.Approved
                || current.RegistrationId is not { } registrationId
            )
            {
                return null;
            }

            var versions = stores.Accreditations.Versions;
            var filter = Builders<AccreditationEntity>.Filter;
            var own = await versions.FindCurrentAsync(
                filter.Eq(a => a.ApplicationId, _applicationId),
                session,
                cancellationToken
            );
            if (own is not null)
            {
                return own.EntityId;
            }

            if (string.IsNullOrWhiteSpace(legacy.AccreditationReference))
            {
                throw new InvalidOperationException(
                    $"Application {_applicationId} was approved without an accreditation number."
                );
            }

            var sameYear = await versions.FindCurrentAsync(
                filter.Eq(a => a.RegistrationId, registrationId)
                    & filter.Eq(a => a.Year, legacy.Year),
                session,
                cancellationToken
            );
            if (sameYear is not null)
            {
                throw new InvalidOperationException(
                    $"Registration {registrationId} already has accreditation {sameYear.EntityId} for {legacy.Year}."
                );
            }

            var overseasSiteIds = entries
                .Where(e => e.OverseasSiteId is not null)
                .GroupBy(e => e.SiteId)
                .ToDictionary(g => g.Key, g => g.First().OverseasSiteId);
            var accreditation = await versions.InsertAsync(
                AccreditationFromApplication.Build(
                    registrationId,
                    legacy,
                    site => overseasSiteIds.GetValueOrDefault(site.SiteId)
                ),
                new ChangeInfo
                {
                    Reason = ChangeReason.ApplicationApproved,
                    ApplicationId = _applicationId,
                },
                session,
                cancellationToken
            );
            await applications.StampDomainModelForeignKeysAsync(
                _applicationId,
                registrationId,
                new Dictionary<string, ObjectId>(),
                session,
                cancellationToken,
                accreditation.EntityId
            );
            return accreditation.EntityId;
        }

        private static ChangeReason WithdrawalChange(DateTime? before, DateTime? after)
        {
            if (before is null && after is not null)
                return ChangeReason.InterimSiteRemoved;
            if (before is not null && after is null)
                return ChangeReason.InterimSiteReadded;
            return ChangeReason.InterimSiteUpdated;
        }

        // An interim proposal of this application that it no longer holds on an ORS it still holds is Discarded.
        // An ORS the application dropped altogether keeps its interim sites as they are; they go with the ORS.
        private async Task DiscardDroppedInterimProposalsAsync(
            List<ApplicationOverseasSiteEntry> entries
        )
        {
            var heldSites = entries.Select(e => e.OverseasSiteId).OfType<ObjectId>().ToHashSet();
            foreach (
                var ors in _byEntityId.Values.Where(s => heldSites.Contains(s.EntityId)).ToList()
            )
            {
                var dropped = ors
                    .InterimSites.Where(IsOwnInterimProposal)
                    .Where(i => !_heldInterims.Contains((ors.EntityId, i.SiteNumber)))
                    .ToList();
                if (dropped.Count == 0)
                {
                    continue;
                }

                dropped.ForEach(i => i.Status = InterimSiteStatus.Discarded);
                Track(
                    (
                        await stores.OverseasSites.Versions.UpdateAsync(
                            ors,
                            ors.Version,
                            Change(
                                ChangeReason.ProposedInterimSiteDiscarded,
                                ors.OrsId,
                                dropped[0].SiteNumber
                            ),
                            session,
                            cancellationToken
                        )
                    ).Entity
                );
            }
        }

        // B8f: a rejected or withdrawn application's Proposed sites and interim sites are Discarded, one ORS version
        // each, unless another application in flight holds them; then they stay Proposed, as the migration decides
        // (§6.1 step 2). Which application proposed them does not matter: a proposal its proposer closed on while
        // this application still held it is this application's to discard. A withdrawn interim site stays
        // Withdrawn, as the migration leaves it.
        private async Task DiscardClosedApplicationsProposalsAsync(
            ObjectId registrationId,
            List<ApplicationOverseasSiteEntry> entries
        )
        {
            var (heldSites, heldInterims) = await HeldInFlightElsewhereAsync(registrationId);
            var (ownSites, ownInterims) = Holdings(entries);
            foreach (var ors in _byEntityId.Values.ToList())
            {
                var discardSite =
                    ors.Status == OverseasSiteStatus.Proposed
                    && ownSites.Contains(ors.EntityId)
                    && !heldSites.Contains(ors.EntityId);
                var interims = ors
                    .InterimSites.Where(i =>
                        i.Status == InterimSiteStatus.Proposed
                        && ownInterims.Contains((ors.EntityId, i.SiteNumber))
                        && !heldInterims.Contains((ors.EntityId, i.SiteNumber))
                    )
                    .ToList();
                if (!discardSite && interims.Count == 0)
                {
                    continue;
                }

                if (discardSite)
                {
                    OverseasSiteTransitions.Registered.EnsureCanTransition(
                        ors.Status,
                        OverseasSiteStatus.Discarded
                    );
                    ors.Status = OverseasSiteStatus.Discarded;
                }

                interims.ForEach(i => i.Status = InterimSiteStatus.Discarded);
                var change = discardSite
                    ? Change(ChangeReason.ProposedSiteDiscarded, ors.OrsId)
                    : Change(
                        ChangeReason.ProposedInterimSiteDiscarded,
                        ors.OrsId,
                        interims[0].SiteNumber
                    );
                Track(
                    (
                        await stores.OverseasSites.Versions.UpdateAsync(
                            ors,
                            ors.Version,
                            change,
                            session,
                            cancellationToken
                        )
                    ).Entity
                );
            }
        }

        // The ORSs, and the interim sites on them, that the registration's other in-flight applications hold. This
        // application is left out by id: its current version still has the status it had before this write.
        private async Task<(
            HashSet<ObjectId> Sites,
            HashSet<(ObjectId OrsId, string SiteNumber)> Interims
        )> HeldInFlightElsewhereAsync(ObjectId registrationId)
        {
            var filter = Builders<ApplicationEntity>.Filter;
            var others = await stores.Applications.Versions.FindAllCurrentAsync(
                filter.Eq(a => a.RegistrationId, registrationId)
                    & filter.Ne(a => a.EntityId, _applicationId)
                    & filter.Nin(a => a.Status, ApplicationGroupMigrator.ClosedStatuses),
                session,
                cancellationToken
            );
            return Holdings(others.SelectMany(a => a.ApplicationOverseasSites));
        }

        // The ORSs, and the interim sites on them, that a set of Application entries holds.
        private static (
            HashSet<ObjectId> Sites,
            HashSet<(ObjectId OrsId, string SiteNumber)> Interims
        ) Holdings(IEnumerable<ApplicationOverseasSiteEntry> entries)
        {
            var held = entries.Where(e => e.OverseasSiteId is not null).ToList();
            return (
                held.Select(e => e.OverseasSiteId!.Value).ToHashSet(),
                held.SelectMany(e =>
                        e.InterimSites.Select(i => (e.OverseasSiteId!.Value, i.SiteNumber))
                    )
                    .ToHashSet()
            );
        }

        // Withdrawn counts: a proposal this application withdrew is still its own to restore.
        private bool IsOwnInterimProposal(InterimSiteEntry interim) =>
            interim.ProposedByApplicationId == _applicationId
            && interim.Status is InterimSiteStatus.Proposed or InterimSiteStatus.Withdrawn;

        private ApplicationOverseasSiteEntry Entry(
            OverseasSiteModel site,
            OverseasSiteEntity? ors,
            BsonDocument? changes
        )
        {
            var entry = LegacyApplicationMapper.ToSiteEntry(
                site,
                ors?.EntityId,
                changes,
                number => LegacySiteMapper.FindInterim(ors, number)?.SiteNumber ?? number
            );
            foreach (var interim in entry.InterimSites)
            {
                interim.ProposedChanges = InterimOverlay(site, ors, interim.LegacySiteId);
            }

            return entry;
        }

        // Option B for interim sites: the overlay holds this application's edits to an interim site it does not own.
        private BsonDocument? InterimOverlay(
            OverseasSiteModel site,
            OverseasSiteEntity? ors,
            int legacySiteId
        )
        {
            var interim = InterimSiteSync.All(site).FirstOrDefault(i => i.SiteId == legacySiteId);
            var stored = interim is null
                ? null
                : LegacySiteMapper.FindInterim(ors, interim.SiteNumber);
            return stored is null || IsOwnInterimProposal(stored)
                ? null
                : LegacySiteMapper.InterimProposedChanges(stored, interim!);
        }

        private ChangeInfo Change(ChangeReason reason, string? orsId, string? siteNumber = null)
        {
            var details = new Dictionary<string, string>();
            if (orsId is not null)
                details["orsId"] = orsId;
            if (siteNumber is not null)
                details["siteNumber"] = siteNumber;
            return new ChangeInfo
            {
                Reason = reason,
                ApplicationId = _applicationId,
                Details = details.Count > 0 ? details : null,
            };
        }
    }
}
