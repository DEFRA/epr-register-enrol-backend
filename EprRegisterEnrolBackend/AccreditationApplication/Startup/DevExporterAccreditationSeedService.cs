using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DevSeed;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Registrations;
using EprRegisterEnrolBackend.Utils;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-425: local manual testing and the fe-tests stack only. Gives each dedicated exporter
// (DevExporterOrg: stub orgs 50018, 50019 for RA-492, 50020 to 50022 for RA-697 and 50026 to 50029 for RA-765, Plastic) an Approved accreditation for
// the current calendar year, and its registration an Approved current version in the domain model,
// so the frontend's material dashboard, manage page and overseas-sites page show content. Development-only, registered with DevSeedData:Enabled
// (the local compose stack, alongside the RA-690 Delta Green seeds) or with ConfigKey alone (the
// fe-tests stack, whose other specs must not see the Delta Green data).
//
// Org 50018's data matches the frontend stub's exporter (stub-manage-accreditations.js): three
// accredited ORSs and one registered only. ORS 001 has a withdrawn interim site, and ORSs 001 and 002 both use
// the same physical site, so the overseas-sites page's per-ORS filtering shows locally. The
// registered-only ORS 004 has an interim site too, which the page must not show. Each copy
// has its own id and number: interim site ids key the edit routes and test ids application-wide,
// and the number allocator keeps numbers unique within an application. Org 50019 (RA-492) has one
// accredited ORS and four registered only: two complete, and two each lacking a different set of
// the fields the in-year "add a registered ORS to your accreditation" journey asks for.
//
// RA-697: org 50018 also has a glass reprocessor registration and accreditation (its Reprocessor),
// matching the frontend stub's, and org 50020 is an exporter that DevChangeRequestSeedService gives no
// change requests. That seed writes only change requests: it never changes these accreditations. Org 50021
// (DevExporterOrg.RegistrationOnly) gets only its Approved registration and four Active domain-model ORS records
// (RegisteredOverseasSites), for the registration-only manage pages; the frontend stub mirrors them. Org 50022
// (DevExporterOrg.NoOverseasSites) gets only its Approved registration, with no ORS records, for the empty tabs.
//
// RA-765: each exporter's applications are its DevExporterOrg.Applications, by year offset from the clock's and
// status. Orgs 50026 to 50029 (DevExporterOrg.Reapply) use them to set which year the manage page's apply banner
// offers: Approved Y-1; Approved Y and Submitted Y+1; Approved Y and Rejected Y+1; Approved Y-2. They have no ORSs.
//
// The year comes from the clock at startup. Idempotent: each application's id is fixed for its
// year, so it is inserted once and left alone after that, including any changes a tester
// makes to it; a registration that already has a current version is left alone.
public class DevExporterAccreditationSeedService(
    IAccreditationApplicationPersistence persistence,
    RegistrationVersionStore registrations,
    OverseasSiteVersionStore overseasSites,
    TimeProvider clock,
    ILogger<DevExporterAccreditationSeedService> logger
) : DevSeedService(logger)
{
    public const string ConfigKey = "DevSeedData:ManageExporterEnabled";

    /// <summary>
    /// Whether this seed runs, which is also whether the stub ReEx registry has the dedicated exporters at all:
    /// anywhere else they would be exporters with no accreditation.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        environment.IsDevelopment()
        && (
            configuration.GetValue<bool>(DevAccreditationHistorySeedService.ConfigKey)
            || configuration.GetValue<bool>(ConfigKey)
        );

    // Each dedicated exporter's ORSs, by the year they are accredited from.
    private static readonly Dictionary<DevExporterOrg, Func<int, List<OverseasSiteModel>>> SitesFor =
        new()
        {
            [DevExporterOrg.Manage] = OverseasSites,
            [DevExporterOrg.InYearChange] = InYearChangeOverseasSites,
            [DevExporterOrg.NoChangeRequests] = NoChangeRequestsOverseasSites,
            [DevExporterOrg.InYearUpdate] = InYearUpdateOverseasSites,
        };

    protected override async Task SeedCoreAsync(CancellationToken cancellationToken)
    {
        var year = clock.GetUtcNow().Year;
        foreach (var org in DevExporterOrg.All)
        {
            foreach (var application in org.Applications)
                await SeedAccreditationAsync(
                    ExporterAccreditation(org, year + application.YearOffset, application.Status),
                    cancellationToken
                );
            await SeedRegistrationAsync(ExporterRegistration(org, year), cancellationToken);
            await SeedOverseasSitesAsync(org, cancellationToken);
            if (org.Reprocessor is null)
                continue;
            await SeedAccreditationAsync(ReprocessorAccreditation(org, year), cancellationToken);
            await SeedRegistrationAsync(ReprocessorRegistration(org, year), cancellationToken);
        }
    }

    private async Task SeedAccreditationAsync(
        AccreditationApplicationModel accreditation,
        CancellationToken cancellationToken
    )
    {
        var existing = await persistence
            .GetByIdAsync(accreditation.OrganisationId, accreditation.ApplicationId!)
            .WaitAsync(cancellationToken);
        if (existing is not null)
        {
            Logger.LogInformation(
                "DevExporterAccreditationSeedService: {Year} {WasteProcessingType} accreditation for org {OrgId} already present",
                accreditation.Year,
                accreditation.WasteProcessingType,
                accreditation.OrgId
            );
            return;
        }

        var created = await persistence.CreateAsync(accreditation).WaitAsync(cancellationToken);
        if (created is null)
        {
            Logger.LogWarning(
                "DevExporterAccreditationSeedService: failed to seed the {Year} {WasteProcessingType} accreditation for org {OrgId}",
                accreditation.Year,
                accreditation.WasteProcessingType,
                accreditation.OrgId
            );
            return;
        }

        Logger.LogInformation(
            "DevExporterAccreditationSeedService: seeded {Year} {WasteProcessingType} accreditation {AccreditationReference}",
            created.Year,
            created.WasteProcessingType,
            created.AccreditationReference
        );
    }

    private async Task SeedRegistrationAsync(
        RegistrationEntity registration,
        CancellationToken cancellationToken
    )
    {
        var seeded = await registrations.SeedIfAbsentAsync(registration, cancellationToken);
        if (seeded)
            Logger.LogInformation(
                "DevExporterAccreditationSeedService: seeded registration {ReExRegistrationId}'s Approved version",
                registration.ReExRegistrationId
            );
        else
            Logger.LogInformation(
                "DevExporterAccreditationSeedService: registration {ReExRegistrationId}'s version already present",
                registration.ReExRegistrationId
            );
    }

    // RA-697: by ORS id, each left alone once it has a current version. Under the registration's current version,
    // which may not be the seeded one's entity.
    private async Task SeedOverseasSitesAsync(DevExporterOrg org, CancellationToken cancellationToken)
    {
        if (!RegisteredOverseasSitesFor.ContainsKey(org))
            return;
        var registration = await registrations.FindCurrentByReExRegistrationIdAsync(
            org.RegistrationId.ToString(),
            cancellationToken
        );
        if (registration is null)
            return;
        var seeded = 0;
        foreach (var site in RegisteredOverseasSites(org, registration.EntityId))
        {
            var existing = await overseasSites.Versions.FindCurrentAsync(
                Builders<OverseasSiteEntity>.Filter.Eq(x => x.RegistrationId, registration.EntityId)
                    & Builders<OverseasSiteEntity>.Filter.Eq(x => x.OrsId, site.OrsId),
                cancellationToken: cancellationToken
            );
            if (existing is not null)
                continue;
            await overseasSites.Versions.InsertAsync(
                site,
                new ChangeInfo { Reason = ChangeReason.ReExImport, ChangedBy = "dev-seed" },
                cancellationToken: cancellationToken
            );
            seeded++;
        }
        Logger.LogInformation(
            "DevExporterAccreditationSeedService: seeded {Seeded} ORS records for registration {ReExRegistrationId}",
            seeded,
            registration.ReExRegistrationId
        );
    }

    // RA-697: the registered ORSs of an org with no accreditation, which are domain-model records rather than an
    // accreditation's sites.
    private static readonly Dictionary<DevExporterOrg, Func<ObjectId, List<OverseasSiteEntity>>> RegisteredOverseasSitesFor =
        new() { [DevExporterOrg.RegistrationOnly] = RegistrationOnlyOverseasSites };

    /// <summary>
    /// RA-697: the org's Active domain-model ORSs under <paramref name="registrationEntityId"/>; none for an org
    /// whose ORSs come from its accreditation.
    /// </summary>
    internal static List<OverseasSiteEntity> RegisteredOverseasSites(DevExporterOrg org, ObjectId registrationEntityId) =>
        RegisteredOverseasSitesFor.TryGetValue(org, out var sites) ? sites(registrationEntityId) : [];

    // Org 50021: four ORSs, two with interim sites and one of those with two. Fixed entity ids: 0f + org id + ORS id.
    private static List<OverseasSiteEntity> RegistrationOnlyOverseasSites(ObjectId registrationEntityId)
    {
        var orgId = DevExporterOrg.RegistrationOnly.OrgId;
        OverseasSiteEntity Site(string orsId, string siteName, string line1, string town, string country, string coordinates, params InterimSiteEntry[] interimSites) =>
            new()
            {
                EntityId = ObjectId.Parse($"0f{orgId:D19}{orsId}"),
                RegistrationId = registrationEntityId,
                OrsId = orsId,
                SiteName = siteName,
                Address = new PostalAddress { Line1 = line1, TownOrCity = town, Country = country },
                FormattedAddress = $"{line1}, {town}, {country}",
                Country = country,
                IsEu = CountryClassifications.IsEu(country),
                IsOecd = CountryClassifications.IsOecd(country),
                Coordinates = coordinates,
                Contact = new ContactPerson
                {
                    FullName = "Site Manager",
                    Email = $"ors{orsId}@registrationonlyexports.example.com",
                    Phone = "+44 1234 567021",
                },
                OperationCodes = ["R3"],
                BaselOecdCodes = ["B3011"],
                RepatriatedLoads = "Rejected loads are returned to the UK exporter.",
                ConditionsOfExport = true,
                Status = OverseasSiteStatus.Active,
                Origin = SiteOrigin.ReEx,
                InterimSites = [.. interimSites],
            };
        InterimSiteEntry Interim(string siteNumber, string siteName, string line1, string town, string country) =>
            new()
            {
                SiteNumber = siteNumber,
                SiteName = siteName,
                Address = new PostalAddress { Line1 = line1, TownOrCity = town, Country = country },
                Contact = new ContactPerson
                {
                    FullName = "Alex Morgan",
                    Email = "alex.morgan@example.com",
                    Phone = "0123456789",
                },
                OperationCodes = ["R13"],
                Status = InterimSiteStatus.Active,
            };
        return
        [
            Site(
                "001",
                "Porto Plasticos Lda",
                "Rua do Porto 21",
                "Porto",
                "Portugal",
                "41.1579, -8.6291",
                Interim("001", "Leixoes Port Storage", "Doca 4", "Matosinhos", "Portugal"),
                Interim("002", "Aveiro Transfer Hub", "Cais 9", "Aveiro", "Portugal")
            ),
            Site(
                "002",
                "Milano Riciclo SpA",
                "Via Industriale 7",
                "Milan",
                "Italy",
                "45.4642, 9.1900",
                Interim("003", "Genoa Dock Store", "Molo 2", "Genoa", "Italy")
            ),
            Site("003", "Penang Polymers Sdn Bhd", "Jalan Pelabuhan 3", "George Town", "Malaysia", "5.4141, 100.3288"),
            Site("004", "Jakarta Daur Ulang PT", "Jalan Pelabuhan 12", "Jakarta", "Indonesia", "-6.2088, 106.8456"),
        ];
    }

    internal static RegistrationEntity ExporterRegistration(DevExporterOrg org, int year) =>
        RegistrationVersionDevSeed.ApprovedRegistration(
            org.OrgId,
            org.RegistrationId,
            org.RegistrationNumberFor(year),
            WasteProcessingKind.Exporter,
            DevExporterOrg.Material
        );

    // RA-697: its own entity id, as the exporter registration's (0e + org id) is one per org.
    internal static RegistrationEntity ReprocessorRegistration(DevExporterOrg org, int year)
    {
        var reprocessor = RequireReprocessor(org);
        var registration = RegistrationVersionDevSeed.ApprovedRegistration(
            org.OrgId,
            reprocessor.RegistrationId,
            reprocessor.RegistrationNumberFor(year),
            WasteProcessingKind.Reprocessor,
            reprocessor.Material
        );
        registration.EntityId = ObjectId.Parse($"0eb{org.OrgId:D21}");
        return registration;
    }

    // "a" + year + padding + org id, e.g. a20260000000000000050018.
    internal static ObjectId ApplicationIdFor(DevExporterOrg org, int year) =>
        ObjectId.Parse($"a{year:D4}{org.OrgId:D19}");

    // RA-697: "b" for the reprocessor, e.g. b20260000000000000050018, so it never collides with the exporter's.
    internal static ObjectId ReprocessorApplicationIdFor(DevExporterOrg org, int year) =>
        ObjectId.Parse($"b{year:D4}{org.OrgId:D19}");

    private static DevReprocessorRegistration RequireReprocessor(DevExporterOrg org) =>
        org.Reprocessor
        ?? throw new ArgumentException($"Org {org.OrgId} has no reprocessor registration.", nameof(org));

    // RA-697: matches the frontend stub's reprocessor on the same org (stub-manage-accreditations.js).
    internal static AccreditationApplicationModel ReprocessorAccreditation(DevExporterOrg org, int year)
    {
        var reprocessor = RequireReprocessor(org);
        var createdAt = new DateTime(year - 1, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        return new AccreditationApplicationModel
        {
            Id = ReprocessorApplicationIdFor(org, year),
            OrganisationId = org.OrganisationId,
            OrgId = org.OrgId,
            OrganisationName = org.Name,
            Year = year,
            RegistrationId = reprocessor.RegistrationId.ToString(),
            IsExporter = false,
            Nation = Nation.England,
            SiteAddress = reprocessor.SiteAddress,
            CompanyRegisterAddressPostcode = org.Postcode,
            CompanyRegisteredAddress = org.RegisteredAddress,
            CompaniesHouseNumber = org.CompaniesHouseNumber,
            PermitNumbers = [org.PermitNumber],
            WasteProcessingType = "reprocessor",
            RegistrationReference = reprocessor.RegistrationNumberFor(year),
            AccreditationReference = reprocessor.AccreditationNumberFor(year),
            MaterialType = reprocessor.Material,
            GlassRecyclingProcess = reprocessor.GlassRecyclingProcess,
            ApplicationStatus = ApplicationStatus.Approved,
            SourceYear = year - 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            DateLastEdited = createdAt,
        };
    }

    // RA-765: only an Approved application has an accreditation number, and an org missing from SitesFor (the
    // reapply orgs) has no ORSs.
    internal static AccreditationApplicationModel ExporterAccreditation(
        DevExporterOrg org,
        int year,
        ApplicationStatus status = ApplicationStatus.Approved
    )
    {
        var createdAt = new DateTime(year - 1, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        return new AccreditationApplicationModel
        {
            Id = ApplicationIdFor(org, year),
            OrganisationId = org.OrganisationId,
            OrgId = org.OrgId,
            OrganisationName = org.Name,
            Year = year,
            RegistrationId = org.RegistrationId.ToString(),
            IsExporter = true,
            Nation = Nation.England,
            CompanyRegisterAddressPostcode = org.Postcode,
            CompanyRegisteredAddress = org.RegisteredAddress,
            CompaniesHouseNumber = org.CompaniesHouseNumber,
            PermitNumbers = [org.PermitNumber],
            WasteProcessingType = "exporter",
            RegistrationReference = org.RegistrationNumberFor(year),
            AccreditationReference =
                status == ApplicationStatus.Approved ? org.AccreditationNumberFor(year) : null,
            MaterialType = DevExporterOrg.Material,
            ApplicationStatus = status,
            SourceYear = year - 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            DateLastEdited = createdAt,
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                SectionStatus = SectionStatus.Completed,
                Sites = SitesFor.TryGetValue(org, out var sites) ? sites(year) : [],
            },
        };
    }

    private static List<OverseasSiteModel> OverseasSites(int year)
    {
        var accreditedFrom = $"{year}-01-01T00:00:00Z";
        var removed = InterimSite(901804, "003", "Old Depot GmbH", "Germany");
        removed.RemovedAt = new DateTime(year, 2, 1, 9, 0, 0, DateTimeKind.Utc);
        List<OverseasSiteModel> sites =
        [
            Ors(
                901801,
                "001",
                "Rotterdam Recycling BV",
                "Industrieweg 44",
                "Netherlands",
                accreditedFrom,
                [
                    InterimSite(901802, "001", "Calais Storage SARL", "France", ["R13"]),
                    AntwerpInterimSite(901803, "002"),
                    removed,
                ]
            ),
            Ors(
                901805,
                "002",
                "Bharat Recycling",
                "12 Ring Road",
                "India",
                accreditedFrom,
                [AntwerpInterimSite(901808, "004")]
            ),
            Ors(
                901806,
                "003",
                "Berlin Steel GmbH",
                "Hafenstrasse 9",
                "Germany",
                accreditedFrom,
                []
            ),
            // Registered only, but its registration lists an interim site: the overseas-sites page
            // must still show no interim sites for it, as they belong to the accreditation.
            Ors(
                901807,
                "004",
                "Dragon Recyclers",
                "8 Harbour Avenue",
                "Vietnam",
                null,
                [InterimSite(901809, "005", "Saigon Port Storage", "Vietnam")]
            ),
        ];
        sites.ForEach(InterimSiteSync.SyncMirror);
        return sites;
    }

    // RA-697 (org 50020): one accredited ORS and one registered only, for the change request pages' empty states.
    // RA-495: the registered one already holds BES evidence on the accreditation, so adding it to the accreditation
    // shows those files read-only beside the request's. No spec relies on it having none (org 50019's Delhi does).
    // The files are records only: nothing is in S3 under their keys.
    private static List<OverseasSiteModel> NoChangeRequestsOverseasSites(int year) =>
        [
            Ors(902001, "001", "Gdansk Recykling Sp. z o.o.", "ul. Portowa 20", "Poland", $"{year}-01-01T00:00:00Z", []),
            WithBesEvidence(
                Ors(902002, "002", "Ghent Recycling NV", "Havenlaan 5", "Belgium", null, []),
                year,
                ("0e000000-0000-4000-8000-000000902002", "ghent-bes-certificate.pdf"),
                ("0e000000-0000-4000-8000-000000902003", "ghent-bes-audit-report.pdf")
            ),
        ];

    private static OverseasSiteModel WithBesEvidence(
        OverseasSiteModel site,
        int year,
        params (string FileId, string Filename)[] files
    )
    {
        site.BesEvidence = new BesEvidenceModel
        {
            BesEvidenceUploads =
            [
                .. files.Select(file => new BesEvidenceFileModel
                {
                    FileId = file.FileId,
                    Filename = file.Filename,
                    ContentType = "application/pdf",
                    ScanStatus = "Clean",
                    BesEvidenceValidFromDate = $"{year}-01-01T00:00:00.000Z",
                    BesEvidenceExpiryDate = $"{year + 2}-12-31T00:00:00.000Z",
                    UploadedAt = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    UploadedBy = "dev-seed",
                    S3Key = $"bes-evidence/accreditation/dev-seed/{site.SiteId}/{file.FileId}",
                }),
            ],
            DoYouWantToUploadMoreEvidence = false,
        };
        return site;
    }

    // validFrom null means registered on the registration but not accredited.
    private static OverseasSiteModel Ors(
        int siteId,
        string orsId,
        string siteName,
        string addressLine1,
        string country,
        string? validFrom,
        List<InterimSiteModel> interimSites
    ) =>
        new()
        {
            SiteId = siteId,
            OrsId = orsId,
            SiteName = siteName,
            AddressLine1 = addressLine1,
            TownOrCity = "Port",
            Country = country,
            SiteAddress = $"{addressLine1}, {country}",
            OperationCodes = ["R3"],
            ValidFrom = validFrom,
            Selected = validFrom is not null,
            IsNewSite = false,
            InterimSites = interimSites,
        };

    // RA-492 (org 50019): one accredited ORS with every field the in-year add journey asks for,
    // and four registered only: two complete (France and, RA-739, the Netherlands with an interim
    // site: EU and OECD, so no BES evidence), listed first so both can be added and submitted
    // together; country only (India: outside the EU and OECD, so BES evidence unless the conditions
    // of export are met); and coordinates only (Turkey: OECD, so no BES evidence). Plus one never
    // registered (see NeverRegisteredOrs), which neither tab lists and which cannot be added to a
    // change request. The Registered tab lists them in this order.
    private static List<OverseasSiteModel> InYearChangeOverseasSites(int year)
    {
        List<OverseasSiteModel> sites =
        [
            InYearChangeOrs(
                Ors(901901, "001", "Hamburg Kunststoff GmbH", "901901 Industrial Estate", "Germany", $"{year}-01-01T00:00:00Z", []),
                isEu: true,
                isOecd: true,
                coordinates: "53.5511, 9.9937",
                operationCodes: ["R3"],
                conditionsOfExport: true
            ),
            InYearChangeOrs(
                Ors(901910, "005", "Lyon Plastiques SA", "901910 Industrial Estate", "France", null, []),
                isEu: true,
                isOecd: true,
                coordinates: "45.7640, 4.8357",
                operationCodes: ["R3"],
                conditionsOfExport: true
            ),
            InYearChangeOrs(
                Ors(
                    901914,
                    "009",
                    "Rotterdam Recycling BV",
                    "901914 Industrial Estate",
                    "Netherlands",
                    null,
                    [AntwerpInterimSite(901915, "001")]
                ),
                isEu: true,
                isOecd: true,
                coordinates: "51.9244, 4.4777",
                operationCodes: ["R3"],
                conditionsOfExport: true
            ),
            InYearChangeOrs(
                Ors(901911, "006", "Delhi Polymers Pvt", "901911 Industrial Estate", "India", null, []),
                isEu: false,
                isOecd: false
            ),
            InYearChangeOrs(
                Ors(901912, "007", "Izmir Recycling AS", "901912 Industrial Estate", "Turkey", null, []),
                isEu: false,
                isOecd: true,
                coordinates: "38.4237, 27.1428"
            ),
            NeverRegisteredOrs(),
        ];
        sites.ForEach(InterimSiteSync.SyncMirror);
        return sites;
    }

    // An ORS the operator added on the accreditation application and then deselected before it was
    // granted: built as AddOverseasSite builds one (IsNewSite set, EU/OECD from the country) and
    // stored unselected, as the overseas-sites PATCH leaves a deselected site. So it was never on
    // the registration, which is why the stub registry's registration does not list it.
    private static OverseasSiteModel NeverRegisteredOrs() =>
        new()
        {
            SiteId = 901913,
            OrsId = "008",
            SiteName = "Valencia Reciclaje SL",
            AddressLine1 = "901913 Industrial Estate",
            TownOrCity = "Valencia",
            Country = "Spain",
            SiteAddress = "901913 Industrial Estate, Valencia, Spain",
            Coordinates = "39.4699, -0.3763",
            ContactName = "Site Manager",
            ContactEmail = "site901913@example.com",
            ContactPhone = "+44 1234 567890",
            OperationCodes = ["R3"],
            Code1 = "B3011",
            RepatriatedLoads = "Rejected loads are returned to the UK exporter.",
            ConditionsOfExport = true,
            IsEu = CountryClassifications.IsEu("Spain"),
            IsOecd = CountryClassifications.IsOecd("Spain"),
            IsNewSite = true,
            Selected = false,
        };

    // Org 50025: three accredited ORSs with every field, whose details an operator changes in year (902501 has
    // two interim sites to change, remove and add to), and two registered ones, complete, to add in the same request.
    // All EU and OECD, so adding one needs no BES evidence. Site ids 9025xx, interim sites 90251x.
    internal static List<OverseasSiteModel> InYearUpdateOverseasSites(int year)
    {
        var accreditedFrom = $"{year}-01-01T00:00:00Z";
        List<OverseasSiteModel> sites =
        [
            InYearChangeOrs(
                Ors(
                    902501,
                    "001",
                    "Gdansk Recykling Sp. z o.o.",
                    "902501 Harbour Estate",
                    "Poland",
                    accreditedFrom,
                    [
                        InterimSite(902511, "001", "Gdynia Transfer Hub", "Poland"),
                        InterimSite(902512, "002", "Szczecin Sorting Centre", "Poland"),
                    ]
                ),
                isEu: true,
                isOecd: true,
                coordinates: "54.3520, 18.6466",
                operationCodes: ["R3"],
                conditionsOfExport: true
            ),
            InYearChangeOrs(
                Ors(902502, "002", "Porto Reciclagem Lda", "902502 Harbour Estate", "Portugal", accreditedFrom, []),
                isEu: true,
                isOecd: true,
                coordinates: "41.1579, -8.6291",
                operationCodes: ["R3"],
                conditionsOfExport: true
            ),
            InYearChangeOrs(
                Ors(902503, "003", "Milano Riciclo SpA", "902503 Harbour Estate", "Italy", accreditedFrom, []),
                isEu: true,
                isOecd: true,
                coordinates: "45.4642, 9.1900",
                operationCodes: ["R3"],
                conditionsOfExport: true
            ),
            InYearChangeOrs(
                Ors(902504, "004", "Ghent Recycling NV", "902504 Harbour Estate", "Belgium", null, []),
                isEu: true,
                isOecd: true,
                coordinates: "51.0543, 3.7174",
                operationCodes: ["R3"],
                conditionsOfExport: true
            ),
            InYearChangeOrs(
                Ors(902505, "005", "Vienna Recycling GmbH", "902505 Harbour Estate", "Austria", null, []),
                isEu: true,
                isOecd: true,
                coordinates: "48.2082, 16.3738",
                operationCodes: ["R3"],
                conditionsOfExport: true
            ),
        ];
        sites.ForEach(InterimSiteSync.SyncMirror);
        return sites;
    }

    // The contact and waste details a registration holds, and only the given coordinates, R codes
    // and conditions of export.
    private static OverseasSiteModel InYearChangeOrs(
        OverseasSiteModel site,
        bool isEu,
        bool isOecd,
        string? coordinates = null,
        List<string>? operationCodes = null,
        bool? conditionsOfExport = null
    )
    {
        site.IsEu = isEu;
        site.IsOecd = isOecd;
        site.Coordinates = coordinates;
        site.OperationCodes = operationCodes ?? [];
        site.ConditionsOfExport = conditionsOfExport;
        site.ContactName = "Site Manager";
        site.ContactEmail = $"site{site.SiteId}@example.com";
        site.ContactPhone = "+44 1234 567890";
        site.Code1 = "B3011";
        site.RepatriatedLoads = "Rejected loads are returned to the UK exporter.";
        return site;
    }

    // One physical site serving two ORSs: a separate copy under each, so withdrawing it from one ORS
    // leaves the other untouched.
    private static InterimSiteModel AntwerpInterimSite(int siteId, string siteNumber) =>
        InterimSite(siteId, siteNumber, "Antwerp Transfer Hub", "Belgium");

    private static InterimSiteModel InterimSite(
        int siteId,
        string siteNumber,
        string siteName,
        string country,
        List<string>? operationCodes = null
    ) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = siteNumber,
            SiteName = siteName,
            Country = country,
            AddressLine1 = "1 Quay Street",
            TownOrCity = "Port",
            ContactName = "Alex Morgan",
            ContactEmail = "alex.morgan@example.com",
            ContactPhone = "0123456789",
            OperationCodes = operationCodes ?? ["R3", "R13"],
        };
}
