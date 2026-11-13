using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DevSeed;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.AccreditationChanges;

// RA-697: local manual testing and the fe-tests stack only. Gives org 50018's current-year accreditations
// (DevExporterAccreditationSeedService's) change requests in every item status and type, so the in-year change
// request pages show content before Case Management can move an item past Submitted. Registered alongside that
// seed, under the same switch, and only with the in-year changes flag on, as the change-request store is.
//
// Exporter: more than a page of items. Each ORS has at most one active accreditation item: ORS 004 Dragon
// (registered only) has the one InProgress item, a New ORS accreditation in the only draft, as RA-492's journey
// would make; the accredited ORSs 001-003 have one ORS update each, Queried, Submitted and Updated, and the tonnage
// band upgrade is Duly made. Granted, refused and withdrawn items make up the history. The exporter registration
// also has one registration-only ORS update (Refused, on Dragon), which its accreditation view merges in: 14
// items. Reprocessor: tonnage band upgrades only.
// Orgs 50019 (RA-492's) and 50020 (the empty states') get none. Org 50021 (registration only) gets five
// registration-only ORS updates (no accreditation), one item each across its seeded ORS records, none In
// progress. Only change requests are written: the accreditations' site data is not changed to match the items'
// outcomes.
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
        PlannedTonnageBand? TonnageBand,
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

    // Oldest first. ValidFrom is when a granted change took effect; a withdrawn update was granted first, but
    // Dragon's withdrawn New ORS accreditation never was (RA-697: its site is registered only).
    private static readonly RequestSeed[] ExporterRequests =
    [
        new(Update, 2, 3, [Ors("001", ChangeItemStatus.Approved, (3, 2)), Ors("002", ChangeItemStatus.Approved, (3, 2))]),
        new(Add, 2, 20, [Ors("004", ChangeItemStatus.Rejected)]),
        new(Tonnage, 3, 10, [Band(PlannedTonnageBand.UpTo5000, ChangeItemStatus.Approved, (4, 1))]),
        new(Add, 4, 6, [Ors("004", ChangeItemStatus.Withdrawn, withdrawnAt: (6, 15))]),
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

    // RA-697 task 014: org 50018's exporter registration also has a registration-only request (no accreditation):
    // an ORS update on Dragon, registered only. Refused, because Dragon's draft is its one active item. Numbered
    // after the exporter's requests.
    private static readonly RequestSeed[] ExporterRegistrationOnlyRequests =
    [
        new(Update, 9, 15, [Ors("004", ChangeItemStatus.Rejected)]),
    ];

    // RA-697: org 50021's, on DevExporterAccreditationSeedService's registered ORS records.
    private static readonly RequestSeed[] RegistrationOnlyRequests =
    [
        new(Update, 2, 10, [Ors("001", ChangeItemStatus.Rejected)]),
        new(Update, 3, 15, [Ors("004", ChangeItemStatus.Approved, (4, 20))]),
        new(Update, 5, 6, [Ors("003", ChangeItemStatus.DulyMade)]),
        new(Update, 6, 18, [Ors("002", ChangeItemStatus.Queried)]),
        new(Update, 8, 25, [Ors("001", ChangeItemStatus.Submitted)]),
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

    // What a seeded request is made against: an accreditation, or (RA-697) a registration with no accreditation.
    private sealed record Target(
        int Year,
        int OrgId,
        string? ApplicationId,
        string? RegistrationId,
        MaterialType MaterialType,
        string? AccreditationReference,
        List<OverseasSiteModel> Sites
    );

    private static Target Accredited(AccreditationApplicationModel accreditation) =>
        new(
            accreditation.Year,
            accreditation.OrgId!.Value,
            accreditation.ApplicationId,
            accreditation.RegistrationId,
            accreditation.MaterialType,
            accreditation.AccreditationReference,
            accreditation.OverseasSites?.Sites ?? []
        );

    // The registration-only view keys sites as the seeded ORS records map; the registration's entity id is not needed.
    private static Target RegistrationOnly(DevExporterOrg org, int year) =>
        new(
            year,
            org.OrgId,
            null,
            org.RegistrationId.ToString(),
            DevExporterOrg.Material,
            null,
            RegisteredSiteMapper.ToSites(
                DevExporterAccreditationSeedService.RegisteredOverseasSites(org, ObjectId.Empty)
            )
        );

    /// <summary>Every seeded change request for the given year, oldest first per accreditation or registration.</summary>
    internal static List<AccreditationChangeRequest> Requests(int year)
    {
        var org = DevExporterOrg.Manage;
        var exporter = DevExporterAccreditationSeedService.ExporterAccreditation(org, year);
        return
        [
            .. Build(Accredited(exporter), 'a', ExporterRequests),
            .. Build(
                Accredited(exporter) with { ApplicationId = null, AccreditationReference = null },
                'a',
                ExporterRegistrationOnlyRequests,
                firstNumber: ExporterRequests.Length + 1
            ),
            .. Build(Accredited(DevExporterAccreditationSeedService.ReprocessorAccreditation(org, year)), 'b', ReprocessorRequests),
            .. Build(RegistrationOnly(DevExporterOrg.RegistrationOnly, year), 'a', RegistrationOnlyRequests),
        ];
    }

    // side: "a" for the exporter and "b" for the reprocessor, as their registration ids start.
    private static IEnumerable<AccreditationChangeRequest> Build(
        Target target,
        char side,
        RequestSeed[] seeds,
        int firstNumber = 1
    ) =>
        seeds.Select((seed, index) => Request(target, side, firstNumber + index, seed));

    private static DateTime SeedDate(int year, (int Month, int Day) date) =>
        new(year, date.Month, date.Day, 9, 0, 0, DateTimeKind.Utc);

    private static ChangeRequestItem Item(
        Target target,
        char side,
        int number,
        RequestSeed seed,
        ItemSeed item,
        int index
    )
    {
        var itemId = Guid.Parse(FixedId(target.Year, target.OrgId, side, number, index + 1));
        DateTime? validFrom = item.ValidFrom is { } from ? SeedDate(target.Year, from) : null;
        if (item.TonnageBand is { } band)
            return new TonnageBandUpgradeItem
            {
                ItemId = itemId,
                RequestedBand = band,
                Status = item.Status,
                ValidFrom = validFrom,
            };
        var site = target.Sites.Single(s => s.OrsId == item.OrsId);
        return new ChangeRequestSite
        {
            ItemId = itemId,
            ChangeType = seed.ChangeType,
            // A registration-only item is keyed by its ORS id's integer value, as the registration view keys
            // its sites, even when the registration's accreditation keys the same ORS differently.
            SiteId = target.ApplicationId is null
                ? int.Parse(site.OrsId!, System.Globalization.CultureInfo.InvariantCulture)
                : site.SiteId,
            OrsId = site.OrsId,
            SiteName = site.SiteName,
            Status = item.Status,
            ValidFrom = validFrom,
            WithdrawnAt = item.WithdrawnAt is { } at ? SeedDate(target.Year, at) : null,
        };
    }

    private static AccreditationChangeRequest Request(Target target, char side, int number, RequestSeed seed)
    {
        var year = target.Year;
        var orgId = target.OrgId;
        var items = seed
            .Items.Select((item, index) => Item(target, side, number, seed, item, index))
            .ToList();

        var createdAt = SeedDate(year, (seed.Month, seed.Day));
        var draft = items.Exists(i => i.Status == ChangeItemStatus.InProgress);
        // RA-739: a submitted request's reference stands in for its submission reference, and each item gets its
        // own, numbered within the request, e.g. CR26050018A008-1.
        var reference = draft ? null : $"CR{year % 100:D2}{orgId:D6}{char.ToUpperInvariant(side)}{number:D3}";
        if (reference is not null)
        {
            for (var index = 0; index < items.Count; index++)
                items[index].CaseManagementReference = $"{reference}-{index + 1}";
        }
        var submittedAt = draft ? (DateTime?)null : createdAt.AddHours(2);
        var updatedAt = items
            .SelectMany(i => new[] { i.ValidFrom, i.WithdrawnAt })
            .Append(submittedAt)
            .Max() ?? createdAt;
        var request = new AccreditationChangeRequest
        {
            RequestId = FixedId(year, orgId, side, number, 0),
            ChangeType = seed.ChangeType,
            OrganisationId = orgId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ApplicationId = target.ApplicationId,
            RegistrationId = target.RegistrationId,
            MaterialType = target.MaterialType,
            Year = year,
            AccreditationReference = target.AccreditationReference,
            Status = draft ? ChangeRequestStatus.InProgress : ChangeRequestStatus.Submitted,
            Sites = [.. items.OfType<ChangeRequestSite>()],
            TonnageBandUpgrade = items.OfType<TonnageBandUpgradeItem>().SingleOrDefault(),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            SubmittedAt = submittedAt,
            SubmittedBy = draft ? null : Submitter,
            SubmissionReference = reference,
        };
        // RA-739: a submitted add-ORS request holds what Submit stores: each site's own charge and the total.
        if (!draft && seed.ChangeType == Add)
            request.SetCharge(ChangeRequestRules.Charge(request.Sites.Count));
        return request;
    }

    // Fixed per year, org, accreditation and request, e.g. 00002026-0000-0000-a000-050018000001 for the exporter's
    // first request and 00002026-0000-0001-a000-050018000001 for its first item. The frontend stub uses the same ids.
    private static string FixedId(int year, int orgId, char side, int request, int item) =>
        $"0000{year:D4}-0000-{item:D4}-{side}000-{orgId:D6}{request:D6}";
}
