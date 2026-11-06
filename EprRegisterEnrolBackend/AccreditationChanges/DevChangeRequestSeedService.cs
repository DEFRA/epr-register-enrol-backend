using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DevSeed;

namespace EprRegisterEnrolBackend.AccreditationChanges;

// RA-697: local manual testing and the fe-tests stack only. Gives org 50018's current-year accreditations
// (DevExporterAccreditationSeedService's) change requests in every item status and type, so the in-year change
// request pages show content before Case Management can move an item past Submitted. Registered alongside that
// seed, under the same switch, and only with the in-year changes flag on, as the change-request store is.
//
// Exporter: more than a page of items. Each ORS has at most one active item: ORS 004 Dragon (registered only)
// has the one InProgress item, a New ORS accreditation in the only draft, as RA-492's journey would make; the
// accredited ORSs 001-003 have one ORS update each, Queried, Submitted and Updated, and the tonnage band upgrade
// is Duly made. Granted, refused and withdrawn items make up the history. Reprocessor: tonnage band upgrades only.
// Orgs 50019 (RA-492's) and 50020 (the empty states') get none. Only change requests are written: the
// accreditations' site data is not changed to match the items' outcomes.
//
// The frontend stub (src/server/common/stub-change-request-seed.js) mirrors these requests; keep the two in step.
//
// The year comes from the clock at startup, and each request is keyed to that year's accreditation. Idempotent:
// each request's id is fixed for the year, so it is inserted once and left alone after that, including any
// change a tester makes to it. The draft is also refused while a tester's own draft is open.
public class DevChangeRequestSeedService(
    IChangeRequestPersistence requests,
    TimeProvider clock,
    ILogger<DevChangeRequestSeedService> logger
) : DevSeedService(logger)
{
    private const string Add = AccreditationChangeRequest.AddOverseasSiteChangeType;
    private const string Update = AccreditationChangeRequest.UpdateOverseasSiteChangeType;
    private const string Tonnage = AccreditationChangeRequest.UpgradeTonnageBandChangeType;

    private sealed record ItemSeed(
        string? OrsId,
        PlannedTonnageBand? Band,
        ChangeItemStatus Status,
        (int Month, int Day)? ValidFrom,
        (int Month, int Day)? WithdrawnAt
    );

    private sealed record RequestSeed(string ChangeType, int Month, int Day, ItemSeed[] Items);

    private static ItemSeed Ors(
        string orsId,
        ChangeItemStatus status,
        (int, int)? validFrom = null,
        (int, int)? withdrawnAt = null
    ) => new(orsId, null, status, validFrom, withdrawnAt);

    private static ItemSeed Band(
        PlannedTonnageBand band,
        ChangeItemStatus status,
        (int, int)? validFrom = null
    ) => new(null, band, status, validFrom, null);

    // Oldest first. ValidFrom is when a granted change took effect; a withdrawn one was granted first.
    private static readonly RequestSeed[] ExporterRequests =
    [
        new(Update, 2, 3, [Ors("001", ChangeItemStatus.Approved, (3, 2)), Ors("002", ChangeItemStatus.Approved, (3, 2))]),
        new(Add, 2, 20, [Ors("004", ChangeItemStatus.Rejected)]),
        new(Tonnage, 3, 10, [Band(PlannedTonnageBand.UpTo5000, ChangeItemStatus.Approved, (4, 1))]),
        new(Add, 4, 6, [Ors("004", ChangeItemStatus.Withdrawn, (5, 4), (6, 15))]),
        new(Update, 5, 12, [Ors("003", ChangeItemStatus.Rejected)]),
        new(Update, 6, 2, [Ors("001", ChangeItemStatus.Withdrawn, (6, 22), (7, 10))]),
        new(Tonnage, 6, 25, [Band(PlannedTonnageBand.Over10000, ChangeItemStatus.Rejected)]),
        new(Update, 7, 14, [Ors("001", ChangeItemStatus.Queried), Ors("002", ChangeItemStatus.Submitted)]),
        new(Update, 8, 20, [Ors("003", ChangeItemStatus.Updated)]),
        new(Tonnage, 9, 8, [Band(PlannedTonnageBand.UpTo10000, ChangeItemStatus.DulyMade)]),
        new(Add, 10, 1, [Ors("004", ChangeItemStatus.InProgress)]),
    ];

    private static readonly RequestSeed[] ReprocessorRequests =
    [
        new(Tonnage, 3, 3, [Band(PlannedTonnageBand.UpTo5000, ChangeItemStatus.Approved, (4, 1))]),
        new(Tonnage, 5, 19, [Band(PlannedTonnageBand.Over10000, ChangeItemStatus.Rejected)]),
        new(Tonnage, 8, 4, [Band(PlannedTonnageBand.UpTo10000, ChangeItemStatus.Submitted)]),
    ];

    private static readonly SubmittedByModel Submitter = new()
    {
        FullName = "Sam Patel",
        JobTitle = "Director",
        Email = "sam.patel@example.com",
    };

    protected override async Task SeedCoreAsync(CancellationToken cancellationToken)
    {
        var year = clock.GetUtcNow().Year;
        var inserted = 0;
        var seeded = Requests(year);
        foreach (var request in seeded)
        {
            if (await requests.InsertAsync(request, cancellationToken))
                inserted++;
        }
        Logger.LogInformation(
            "DevChangeRequestSeedService: seeded {Inserted} of {Total} {Year} change requests; the rest were already present",
            inserted,
            seeded.Count,
            year
        );
    }

    /// <summary>Every seeded change request for the given year's accreditations, oldest first per accreditation.</summary>
    internal static List<AccreditationChangeRequest> Requests(int year)
    {
        var org = DevExporterOrg.Manage;
        return
        [
            .. Build(DevExporterAccreditationSeedService.ExporterAccreditation(org, year), 'a', ExporterRequests),
            .. Build(DevExporterAccreditationSeedService.ReprocessorAccreditation(org, year), 'b', ReprocessorRequests),
        ];
    }

    // side: "a" for the exporter and "b" for the reprocessor, as their registration ids start.
    private static IEnumerable<AccreditationChangeRequest> Build(
        AccreditationApplicationModel accreditation,
        char side,
        RequestSeed[] seeds
    ) =>
        seeds.Select((seed, index) => Request(accreditation, side, index + 1, seed));

    private static AccreditationChangeRequest Request(
        AccreditationApplicationModel accreditation,
        char side,
        int number,
        RequestSeed seed
    )
    {
        var year = accreditation.Year;
        DateTime On((int Month, int Day) date) => new(year, date.Month, date.Day, 9, 0, 0, DateTimeKind.Utc);
        var orgId = accreditation.OrgId!.Value;
        var items = seed
            .Items.Select<ItemSeed, ChangeRequestItem>(
                (item, index) =>
                {
                    var itemId = Guid.Parse(FixedId(year, orgId, side, number, index + 1));
                    DateTime? validFrom = item.ValidFrom is { } from ? On(from) : null;
                    if (item.Band is { } band)
                        return new TonnageBandUpgradeItem
                        {
                            ItemId = itemId,
                            RequestedBand = band,
                            Status = item.Status,
                            ValidFrom = validFrom,
                        };
                    var site = accreditation.OverseasSites!.Sites.Single(s => s.OrsId == item.OrsId);
                    return new ChangeRequestSite
                    {
                        ItemId = itemId,
                        ChangeType = seed.ChangeType,
                        SiteId = site.SiteId,
                        OrsId = site.OrsId,
                        SiteName = site.SiteName!,
                        Status = item.Status,
                        ValidFrom = validFrom,
                        WithdrawnAt = item.WithdrawnAt is { } at ? On(at) : null,
                    };
                }
            )
            .ToList();

        var createdAt = On((seed.Month, seed.Day));
        var draft = items.Exists(i => i.Status == ChangeItemStatus.InProgress);
        var submittedAt = draft ? (DateTime?)null : createdAt.AddHours(2);
        var updatedAt = items
            .SelectMany(i => new[] { i.ValidFrom, i.WithdrawnAt })
            .Append(submittedAt)
            .Max() ?? createdAt;
        return new AccreditationChangeRequest
        {
            RequestId = FixedId(year, orgId, side, number, 0),
            ChangeType = seed.ChangeType,
            OrganisationId = accreditation.OrganisationId,
            ApplicationId = accreditation.ApplicationId!,
            RegistrationId = accreditation.RegistrationId,
            MaterialType = accreditation.MaterialType,
            Year = year,
            AccreditationReference = accreditation.AccreditationReference,
            Status = draft ? ChangeRequestStatus.InProgress : ChangeRequestStatus.Submitted,
            Sites = [.. items.OfType<ChangeRequestSite>()],
            TonnageBandUpgrade = items.OfType<TonnageBandUpgradeItem>().SingleOrDefault(),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            SubmittedAt = submittedAt,
            SubmittedBy = draft ? null : Submitter,
            CaseManagementReference = draft
                ? null
                : $"CR{year % 100:D2}{orgId:D6}{char.ToUpperInvariant(side)}{number:D3}",
        };
    }

    // Fixed per year, org, accreditation and request, e.g. 00002026-0000-0000-a000-050018000001 for the exporter's
    // first request and 00002026-0000-0001-a000-050018000001 for its first item. The frontend stub uses the same ids.
    private static string FixedId(int year, int orgId, char side, int request, int item) =>
        $"0000{year:D4}-0000-{item:D4}-{side}000-{orgId:D6}{request:D6}";
}
