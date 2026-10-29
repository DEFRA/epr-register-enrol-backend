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
    ApplicationGroupStores stores
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

        var run = new SyncRun(stores, applications, session, legacy, current, cancellationToken);
        var entries = await run.SyncSitesAsync();

        var next = LegacyApplicationMapper.ToEntity(legacy, current.RegistrationId, entries);
        next.AccreditationId = current.AccreditationId;
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
    }

    /// <summary>One sync of one application. Never shared between calls.</summary>
    private sealed class SyncRun(
        ApplicationGroupStores stores,
        IAccreditationApplicationPersistence applications,
        IClientSessionHandle session,
        AccreditationApplicationModel legacy,
        ApplicationEntity current,
        CancellationToken cancellationToken
    )
    {
        private readonly ObjectId _applicationId = current.EntityId;
        private readonly Dictionary<string, OverseasSiteEntity> _byOrsId = [];
        private readonly Dictionary<ObjectId, OverseasSiteEntity> _byEntityId = [];

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
            var entries = new List<ApplicationOverseasSiteEntry>();
            var stamps = new Dictionary<string, ObjectId>();
            foreach (var site in Sites)
            {
                var ors = Find(site, previous.GetValueOrDefault(site.SiteId));
                BsonDocument? changes = null;
                if (ors is null)
                {
                    ors = site.OrsId is null ? null : await CreateAsync(registrationId, site);
                }
                else if (IsOwnProposal(ors))
                {
                    ors = await WriteThroughAsync(ors, site);
                }
                else
                {
                    changes = LegacySiteMapper.ProposedChanges(ors, site);
                }

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

            await DiscardRemovedProposalsAsync(entries);
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

        private void Track(OverseasSiteEntity ors)
        {
            _byEntityId[ors.EntityId] = ors;
            if (ors.OrsId is not null)
            {
                _byOrsId[ors.OrsId] = ors;
            }
        }

        // A site with no orsId (legacy data only) is found through the ORS its entry already points at.
        private OverseasSiteEntity? Find(
            OverseasSiteModel site,
            ObjectId? previousOverseasSiteId
        ) =>
            site.OrsId is not null ? _byOrsId.GetValueOrDefault(site.OrsId)
            : previousOverseasSiteId is { } id ? _byEntityId.GetValueOrDefault(id)
            : null;

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

        private static ApplicationOverseasSiteEntry Entry(
            OverseasSiteModel site,
            OverseasSiteEntity? ors,
            BsonDocument? changes
        ) =>
            LegacyApplicationMapper.ToSiteEntry(
                site,
                ors?.EntityId,
                changes,
                number =>
                    ors?.InterimSites.Find(i =>
                        i.SiteNumber == number || i.PreviousSiteNumbers.Contains(number)
                    )?.SiteNumber
                    ?? number
            );

        private ChangeInfo Change(ChangeReason reason, string? orsId) =>
            new()
            {
                Reason = reason,
                ApplicationId = _applicationId,
                Details = orsId is null
                    ? null
                    : new Dictionary<string, string> { ["orsId"] = orsId },
            };
    }
}
