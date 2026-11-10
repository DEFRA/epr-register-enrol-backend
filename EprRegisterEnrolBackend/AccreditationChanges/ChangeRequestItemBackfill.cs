using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationChanges;

// RA-697: bound from Migrations:Ra697ChangeRequestItemBackfill. Every flag defaults off, so the backfill is a no-op
// in every environment until deliberately switched on.
public sealed class ChangeRequestItemBackfillOptions
{
    public const string SectionName = "Migrations:Ra697ChangeRequestItemBackfill";

    // Off: the backfill does nothing at all (not even a read).
    public bool Enabled { get; set; }

    // Off (with Enabled on): dry run - logs each request it would backfill, writes nothing.
    public bool Apply { get; set; }
}

// RA-697: gives the items of change requests stored before RA-697 the id and status every item now has. Those requests'
// sites were stored with neither, so they read with the model's defaults: a new random ItemId on every read (no item
// can be addressed by its id) and InProgress (wrong for a submitted request's items). One-off, like
// AccreditationReferenceCorrectionMigration, and registered only with the in-year changes flag, like the change-request
// store: with the flag off the collection is never touched.
//
// For each site missing an itemId and/or a status, sets only what is missing - an existing id or status (a decided
// item's, say) is kept:
//   - itemId: a new Guid, stored as the model stores it (standard UUID binary).
//   - status: the request's own status, the one a pre-RA-697 request's items all shared:
//       request InProgress (or no status, which the model reads as InProgress) -> item InProgress
//       request Submitted                                                      -> item Submitted
//     ChangeRequestStatus has no other value. A request holding anything else cannot say what its items are, so it
//     is logged and left alone.
// Tonnage band upgrades arrived with RA-697 and have always been stored with both fields, so only sites are filled.
//
// Raw BSON throughout, so what is missing is read from the stored document rather than hidden by the model's
// defaults. Idempotent: it only finds requests with a site still missing a field. Each request is one update guarded
// on the version read and bumping it, as ChangeRequestPersistence.UpdateAsync does, so a concurrent save is never
// clobbered (that request is logged and left for the next run) and a copy read before the backfill, with its random
// ids, can no longer be saved over it. UpdatedAt is left alone: this is not a change to the request.
//
// A BackgroundService like the RA-691 migration, so a Mongo failure here logs rather than crash-looping the host.
public class ChangeRequestItemBackfill(
    IMongoDbClientFactory mongo,
    IOptions<ChangeRequestItemBackfillOptions> options,
    ILogger<ChangeRequestItemBackfill> logger
) : BackgroundService
{
    internal enum RunResult
    {
        Disabled,
        DryRun,
        Applied,
    }

    /// <summary>
    /// What a run did (or, dry, would do): the requests and items it backfilled, and the requests it left alone (an
    /// unknown request status, or changed while it ran).
    /// </summary>
    internal sealed record Outcome(RunResult Result, int Requests, int Items, int Skipped);

    private const string SitesField = "sites";
    private const string ItemIdField = "itemId";
    private const string StatusField = "status";
    private const string VersionField = "version";

    private static readonly IReadOnlyDictionary<string, string> ItemStatusForRequestStatus = new Dictionary<
        string,
        string
    >
    {
        [nameof(ChangeRequestStatus.InProgress)] = nameof(ChangeItemStatus.InProgress),
        [nameof(ChangeRequestStatus.Submitted)] = nameof(ChangeItemStatus.Submitted),
    };

    private static FilterDefinitionBuilder<BsonDocument> Filter => Builders<BsonDocument>.Filter;

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
            // Each request is written on its own and only where still incomplete, so the next run carries on safely.
            logger.LogError(ex, "RA-697 change request item backfill failed");
        }
    }

    // Extracted so tests can await the run deterministically - BackgroundService's StartAsync only kicks ExecuteAsync
    // off as a background task.
    internal async Task<Outcome> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
            return new Outcome(RunResult.Disabled, 0, 0, 0);

        var apply = settings.Apply;
        var mode = apply ? "APPLY" : "DRY RUN (nothing will be written)";
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("RA-697 change request item backfill starting. Mode={Mode}", mode);

        var collection = mongo.GetCollection<BsonDocument>(ChangeRequestPersistence.CollectionName);
        var incomplete = Filter.ElemMatch<BsonDocument>(
            SitesField,
            Filter.Or(Filter.Exists(ItemIdField, false), Filter.Exists(StatusField, false))
        );

        var requests = 0;
        var items = 0;
        var skipped = 0;
        using var cursor = await collection.Find(incomplete).ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var request in cursor.Current)
            {
                var filled = await BackfillAsync(collection, request, mode, apply, cancellationToken);
                if (filled is null)
                {
                    skipped++;
                    continue;
                }
                requests++;
                items += filled.Value;
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-697 change request item backfill complete. Mode={Mode} Requests={Requests} Items={Items} "
                    + "Skipped={Skipped}",
                mode,
                requests,
                items,
                skipped
            );
        return new Outcome(apply ? RunResult.Applied : RunResult.DryRun, requests, items, skipped);
    }

    // Fills one request's incomplete sites and, when applying, writes them; the number of sites filled, or null when
    // the request is left alone.
    private async Task<int?> BackfillAsync(
        IMongoCollection<BsonDocument> collection,
        BsonDocument request,
        string mode,
        bool apply,
        CancellationToken cancellationToken
    )
    {
        var requestId = request["_id"];
        var requestStatus = request.TryGetValue(StatusField, out var status)
            ? status.ToString()!
            : nameof(ChangeRequestStatus.InProgress);
        if (!ItemStatusForRequestStatus.TryGetValue(requestStatus, out var itemStatus))
        {
            logger.LogWarning(
                "RA-697 change request item backfill: request {RequestId} has status {Status}, which says nothing "
                    + "about its items' status; left alone",
                requestId,
                requestStatus
            );
            return null;
        }

        var sites = request[SitesField].AsBsonArray;
        var filled = 0;
        foreach (var site in sites.OfType<BsonDocument>())
        {
            var missing = !site.Contains(ItemIdField) || !site.Contains(StatusField);
            if (!site.Contains(ItemIdField))
                site[ItemIdField] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard);
            if (!site.Contains(StatusField))
                site[StatusField] = itemStatus;
            if (missing)
                filled++;
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "RA-697 {Mode}: request {RequestId} ({Status}): {Items} item(s) backfilled",
                mode,
                requestId,
                requestStatus,
                filled
            );
        if (!apply)
            return filled;

        var result = await collection.UpdateOneAsync(
            Filter.Eq("_id", requestId)
                & Filter.Eq(VersionField, request.GetValue(VersionField, BsonNull.Value)),
            Builders<BsonDocument>.Update.Set(SitesField, sites).Inc(VersionField, 1L),
            cancellationToken: cancellationToken
        );
        if (result.ModifiedCount == 1)
            return filled;

        logger.LogWarning(
            "RA-697 change request item backfill: request {RequestId} changed while it was being backfilled, so "
                + "nothing was written to it; the next run picks it up",
            requestId
        );
        return null;
    }
}
