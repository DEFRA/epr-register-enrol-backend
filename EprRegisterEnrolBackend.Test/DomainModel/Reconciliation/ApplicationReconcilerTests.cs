using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using EprRegisterEnrolBackend.DomainModel.Reconciliation;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Reconciliation;

/// <summary>
/// A legacy application, migrated for real and read back through the projection, must match its document. The fixture
/// fills every field the model holds, so a field the projection loses shows up as a difference.
/// </summary>
public sealed class ApplicationReconcilerTests : IDisposable
{
    private const string ReExOrganisationId = "6512a0f0c0ffee0000000001";
    private const string ReExRegistrationId = "6512a0f0c0ffee0000000002";
    private const string LegacyCollection = "accreditationApplications";
    private static readonly DateTime Day1 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly AccreditationApplicationPersistence _applications;
    private readonly ApplicationGroupMigrator _migrator;
    private readonly ApplicationReconciler _reconciler;
    private readonly ApplicationGroupStores _stores;

    public ApplicationReconcilerTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("reconcile");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        var clock = new TestClock(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        var logs = NullLoggerFactory.Instance;
        _applications = new AccreditationApplicationPersistence(_factory, logs);
        var stores = new ApplicationGroupStores(
            new OrganisationPersistence(_factory, logs),
            new RegistrationPersistence(_factory, logs, clock),
            new OverseasSitePersistence(_factory, logs, clock),
            new ApplicationVersionPersistence(_factory, logs, clock),
            new AccreditationPersistence(_factory, logs, clock),
            new MigrationConflictPersistence(_factory, logs)
        );
        _migrator = new ApplicationGroupMigrator(_applications, stores, clock);
        _stores = stores;
        _reconciler = new ApplicationReconciler(
            _factory,
            _applications,
            new ApplicationReadModel(stores),
            stores.Conflicts
        );
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static InterimSiteModel Interim(
        int siteId,
        string number,
        DateTime? removedAt = null
    ) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = number,
            Country = "Belgium",
            SiteName = "Interim",
            AddressLine1 = "1 Quai",
            AddressLine2 = "Dock 4",
            TownOrCity = "Antwerp",
            StateOrRegion = "Flanders",
            Postcode = "2000",
            ContactName = "Interim Contact",
            ContactEmail = "interim@example.com",
            ContactPhone = "000",
            OperationCodes = ["R3"],
            IsNewSite = false,
            CreatedAt = Day1,
            RemovedAt = removedAt,
        };

    private static BesEvidenceModel Evidence() =>
        new()
        {
            DoYouWantToUploadMoreEvidence = false,
            BesEvidenceUploads =
            [
                new BesEvidenceFileModel
                {
                    FileId = "file-1",
                    Filename = "evidence.pdf",
                    ContentType = "application/pdf",
                    ScanStatus = "Clean",
                    BesEvidenceValidFromDate = "2026-01-01",
                    BesEvidenceExpiryDate = "2027-01-01",
                    UploadedAt = Day1,
                    UploadedBy = "user-1",
                    S3Key = "key",
                    S3Bucket = "bucket",
                },
            ],
        };

    private static OverseasSiteModel Site(int siteId, string? orsId, string name) =>
        new()
        {
            SiteId = siteId,
            OrsId = orsId,
            SiteName = name,
            SiteAddress = "1 Rue, Lille, France",
            AddressLine1 = "1 Rue",
            AddressLine2 = "Zone 2",
            TownOrCity = "Lille",
            Country = "France",
            Coordinates = "50.6,3.0",
            ContactName = "Site Contact",
            ContactEmail = "site@example.com",
            ContactPhone = "111",
            OperationCodes = ["R3", "R4"],
            Code1 = "B3011",
            Code2 = "B3020",
            Code3 = "B3030",
            RepatriatedLoads = "Returned",
            ConditionsOfExport = true,
            IsEu = true,
            IsOecd = true,
            Selected = true,
            BesEvidence = Evidence(),
            IsNewSite = false,
            RegisteredNowAccredited = true,
        };

    private static AccreditationApplicationFile File() =>
        new()
        {
            FileId = "plan-1",
            Filename = "plan.pdf",
            ContentType = "application/pdf",
            UploadedAt = Day1,
            UploadedByUserId = "user-1",
            ScanStatus = FileScanStatus.Clean,
            S3Key = "key",
            S3Bucket = "bucket",
        };

    private static AccreditationApplicationModel Application(
        ApplicationStatus status = ApplicationStatus.Queried,
        DateTime? updatedAt = null,
        int year = 2027,
        params OverseasSiteModel[] sites
    )
    {
        var when = updatedAt ?? Day1;
        return new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = ReExOrganisationId,
            OrgId = 500500,
            OrganisationName = "Acme Recycling Ltd",
            CompaniesHouseNumber = "01234567",
            CompanyRegisteredAddress = "1 High St, Leeds, LS1 1AA",
            CompanyRegisterAddressPostcode = "LS1 1AA",
            IsUkRegisteredAddress = true,
            SubmitterContactDetails = new SubmitterContactDetailsModel
            {
                FullName = "Org Submitter",
                Email = "org@example.com",
                Phone = "222",
                JobTitle = "Manager",
            },
            SiteAddress = "2 Mill Rd, Leeds, LS2 2BB",
            PermitNumbers = ["EPR/AB1234", "EPR/CD5678"],
            SourceReExAccreditationId = "6512a0f0c0ffee0000000009",
            SourceYear = year - 1,
            PreviousAccreditationNumbers = ["A26EX5005000001PL"],
            RegistrationId = ReExRegistrationId,
            RegistrationReference = "R27EX1234567P",
            WasteProcessingType = "exporter",
            IsExporter = true,
            Nation = Nation.England,
            MaterialType = MaterialType.Glass,
            GlassRecyclingProcess = GlassRecyclingProcess.Remelt,
            Year = year,
            ApplicationStatus = status,
            ApplicationReference = $"APP-{year}",
            PaymentReference = "PR/PK/REP/500500",
            CaseManagementWorkItemId = Guid.NewGuid(),
            CaseManagementStatusUpdatedAt = Day1,
            AccreditationReference = $"A{year}EX5005000001PL",
            SubmittedBy = new SubmittedByModel
            {
                FullName = "Submitter",
                JobTitle = "Director",
                Email = "submitter@example.com",
            },
            WithdrawalReason = "Reason",
            DateSent = Day1,
            DateLastEdited = when,
            CreatedAt = when,
            UpdatedAt = when,
            Prns = new AccreditationApplicationPrns
            {
                PlannedTonnageBand = PlannedTonnageBand.UpTo5000,
                Authorisers =
                [
                    new PrnsAuthoriser
                    {
                        FullName = "Authoriser",
                        Email = "auth@example.com",
                        IsNew = true,
                    },
                ],
                SectionStatus = SectionStatus.Submitted,
                Versions =
                [
                    new PrnsSnapshot
                    {
                        PlannedTonnageBand = PlannedTonnageBand.UpTo500,
                        VersionedAt = Day1,
                    },
                ],
            },
            BusinessPlan = new AccreditationApplicationBusinessPlan
            {
                NewInfrastructurePercent = 40,
                PriceSupportPercent = 60,
                NewInfrastructureDetail = "Detail",
                SectionStatus = SectionStatus.Completed,
                Versions =
                [
                    new BusinessPlanSnapshot { NewInfrastructurePercent = 10, VersionedAt = Day1 },
                ],
            },
            SamplingPlan = new AccreditationApplicationSamplingPlan
            {
                Files = [File()],
                SectionStatus = SectionStatus.Completed,
                Versions = [new SamplingPlanSnapshot { Files = [File()], VersionedAt = Day1 }],
            },
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [.. sites],
                SectionStatus = SectionStatus.Completed,
                Versions =
                [
                    new OverseasSitesSnapshot
                    {
                        VersionedAt = Day1,
                        Sites = [Site(9, "009", "Snapshot site")],
                    },
                ],
            },
            BesEvidence = new AccreditationApplicationBesEvidence
            {
                SectionStatus = SectionStatus.Completed,
                Versions = [new BesEvidenceSnapshot { VersionedAt = Day1 }],
            },
            Query = new AccreditationApplicationQuery
            {
                QueryNote = "Please check",
                QueriedSectionKeys = ["prns"],
                QuerySubmissions =
                [
                    new QuerySubmission
                    {
                        QuerySubmissionTime = Day1,
                        SectionKeys = ["prns"],
                        QuerySubmitterContactDetails = new QuerySubmitterContactDetails
                        {
                            FullName = "Regulator",
                            Email = "reg@example.com",
                            Role = "Officer",
                        },
                    },
                ],
            },
        };
    }

    private async Task<ObjectId> CreateAsync(AccreditationApplicationModel application) =>
        (await _applications.CreateAsync(application))!.Id!.Value;

    private async Task MigrateAsync(ObjectId id) =>
        await InsertRaceRetry.RunInTransactionAsync(
            _factory,
            (session, token) => _migrator.EnsureMigratedAsync(id, session, token),
            _ => { },
            Ct
        );

    private Task<UpdateResult> EditLegacyAsync(ObjectId id, BsonDocument set) =>
        _factory
            .GetCollection<BsonDocument>(LegacyCollection)
            .UpdateOneAsync(
                new BsonDocument("_id", id),
                new BsonDocument("$set", set),
                cancellationToken: Ct
            );

    [Fact]
    public async Task Run_AFullyPopulatedApplication_MatchesAfterMigration()
    {
        var site = Site(1, "001", "Plant");
        site.InterimSites = [Interim(1, "001"), Interim(2, "002", removedAt: Day1)];
        InterimSiteSync.SyncMirror(site);
        site.PreviousSites = [Site(7, "007", "Before the promote")];
        var id = await CreateAsync(Application(sites: site));
        await MigrateAsync(id);

        var report = await _reconciler.RunAsync(Ct);

        report.DifferencesByField.Should().BeEmpty();
        report.Matching.Should().Be(1);
        report.WithDifferences.Should().Be(0);
    }

    [Fact]
    public async Task Run_AnInFlightApplicationProposingChanges_MatchesThroughTheOverlay()
    {
        var approved = Application(ApplicationStatus.Approved, Day1, 2026, Site(1, "001", "Plant"));
        var changed = Site(1, "001", "Plant renamed");
        changed.ContactName = "Someone else";
        changed.OperationCodes = ["R5"];
        var inFlight = Application(ApplicationStatus.Started, Day1.AddDays(1), 2027, changed);
        var id = await CreateAsync(approved);
        await CreateAsync(inFlight);
        await MigrateAsync(id);

        var report = await _reconciler.RunAsync(Ct);

        report.DifferencesByField.Should().BeEmpty();
        report.Matching.Should().Be(2);
    }

    [Fact]
    public async Task Run_ARenumberedInterimSite_IsNotADifference()
    {
        var site = Site(1, "001", "Plant");
        site.InterimSites = [Interim(1, "SN-0001")];
        InterimSiteSync.SyncMirror(site);
        var id = await CreateAsync(Application(sites: site));
        await MigrateAsync(id);

        var report = await _reconciler.RunAsync(Ct);

        report.DifferencesByField.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_APreRa603SingularInterimSite_IsNotADifference()
    {
        var site = Site(1, "001", "Plant");
        site.InterimSite = Interim(1, "001");
        var id = await CreateAsync(Application(sites: site));
        await MigrateAsync(id);

        var report = await _reconciler.RunAsync(Ct);

        report.DifferencesByField.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_LegacyChangedWithoutTheModel_ReportsTheFieldAndNotTheValue()
    {
        var id = await CreateAsync(Application(sites: Site(1, "001", "Plant")));
        await MigrateAsync(id);
        await EditLegacyAsync(
            id,
            new BsonDocument
            {
                { "paymentReference", "SECRET-VALUE" },
                { "overseasSites.sites.0.siteName", "SECRET-NAME" },
            }
        );

        var report = await _reconciler.RunAsync(Ct);

        report.WithDifferences.Should().Be(1);
        report.Matching.Should().Be(0);
        report.SampleApplicationIds.Should().Equal(id);
        report
            .DifferencesByField.Should()
            .BeEquivalentTo(
                new Dictionary<string, long>
                {
                    ["paymentReference"] = 1,
                    ["overseasSites.sites[].siteName"] = 1,
                }
            );
        report.ToLogLine().Should().NotContain("SECRET");
    }

    [Theory]
    [InlineData("2026-01-01T00:00:00.000Z")]
    [InlineData("2026-01-01")]
    [InlineData("2026-01-01T01:00:00+01:00")]
    public async Task Run_ASiteValidFrom_MatchesAsTheSameInstant(string validFrom)
    {
        var site = Site(1, "001", "Plant");
        site.ValidFrom = validFrom;
        var id = await CreateAsync(Application(sites: site));
        await MigrateAsync(id);

        var report = await _reconciler.RunAsync(Ct);

        report.Matching.Should().Be(1);
        report.DifferencesByField.Should().BeEmpty();
        report.ModelOnlyByField.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_ASiteValidFromChangedOnlyInLegacy_IsADifference()
    {
        var site = Site(1, "001", "Plant");
        site.ValidFrom = "2026-01-01T00:00:00.000Z";
        var id = await CreateAsync(Application(sites: site));
        await MigrateAsync(id);
        await EditLegacyAsync(
            id,
            new BsonDocument("overseasSites.sites.0.validFrom", "2026-06-01T00:00:00.000Z")
        );

        var report = await _reconciler.RunAsync(Ct);

        report.DifferencesByField.Should().ContainKey("overseasSites.sites[].validFrom");
    }

    [Fact]
    public async Task Run_AnApprovalHereWithNoReExDate_IsNotServedAsALegacyDate()
    {
        var site = Site(1, "001", "Plant");
        var application = Application(ApplicationStatus.Approved, sites: site);
        var id = await CreateAsync(application);
        await MigrateAsync(id);

        var report = await _reconciler.RunAsync(Ct);

        report.ModelOnlyByField.Should().BeEmpty();
        report.Matching.Should().Be(1);
    }

    [Fact]
    public async Task Run_AnOlderApplicationWithABlankCopy_IsFlaggedAsModelOnly()
    {
        var older = Application(ApplicationStatus.Started, Day1, 2026, Site(1, "001", "Plant"));
        older.PermitNumbers = [];
        older.SubmitterContactDetails = null;
        var newer = Application(
            ApplicationStatus.Started,
            Day1.AddDays(1),
            2027,
            Site(1, "001", "Plant")
        );
        await CreateAsync(older);
        var id = await CreateAsync(newer);
        await MigrateAsync(id);

        var report = await _reconciler.RunAsync(Ct);

        report
            .ModelOnlyByField.Should()
            .BeEquivalentTo(
                new Dictionary<string, long>
                {
                    ["permitNumbers"] = 1,
                    ["submitterContactDetails"] = 1,
                }
            );
        report.WithDifferences.Should().Be(1);
        report.SampleApplicationIds.Should().Equal(older.Id!.Value);
    }

    [Fact]
    public async Task Run_AValueOnlyInTheModel_IsNotAcceptedEvenWhenListedAsAConflict()
    {
        var older = Application(ApplicationStatus.Started, Day1, 2026, Site(1, "001", "Plant"));
        older.CompanyRegisteredAddress = "Older address";
        older.PermitNumbers = [];
        var newer = Application(
            ApplicationStatus.Started,
            Day1.AddDays(1),
            2027,
            Site(1, "001", "Plant")
        );
        await CreateAsync(older);
        var id = await CreateAsync(newer);
        await MigrateAsync(id);

        var report = await _reconciler.RunAsync(Ct);

        report.AcceptedConflicts.Should().Be(0);
        report.WithDifferences.Should().Be(1);
        report.ModelOnlyByField.Should().ContainKey("permitNumbers");
        report.DifferencesByField.Should().ContainKey("companyRegisteredAddress");
    }

    [Fact]
    public async Task Run_ADifferenceAlreadyListedAsAConflict_IsCountedAsAcceptedNotADifference()
    {
        var older = Application(ApplicationStatus.Started, Day1, 2026, Site(1, "001", "Plant"));
        older.CompanyRegisteredAddress = "Older address";
        var newer = Application(
            ApplicationStatus.Started,
            Day1.AddDays(1),
            2027,
            Site(1, "001", "Plant")
        );
        await CreateAsync(older);
        var id = await CreateAsync(newer);
        await MigrateAsync(id);

        var report = await _reconciler.RunAsync(Ct);

        report.AcceptedConflicts.Should().Be(1);
        report.Matching.Should().Be(1);
        report.WithDifferences.Should().Be(0);
        report.DifferencesByField.Should().BeEmpty();
        report.ToLogLine().Should().Contain("acceptedConflicts=1");
    }

    [Fact]
    public async Task Run_AnApplicationThatMatchesButIsListed_IsMatchingNotAccepted()
    {
        var id = await CreateAsync(Application(sites: Site(1, null!, "No ORS id")));
        await MigrateAsync(id);

        var report = await _reconciler.RunAsync(Ct);

        report.Matching.Should().Be(1);
        report.AcceptedConflicts.Should().Be(0);
    }

    [Fact]
    public async Task Run_AnUnexpectedErrorOnOneApplication_IsCountedAndTheRunCarriesOn()
    {
        var bad = await CreateAsync(
            Application(ApplicationStatus.Started, Day1, 2026, Site(1, "001", "Plant"))
        );
        var fine = await CreateAsync(
            Application(ApplicationStatus.Started, Day1, 2027, Site(1, "001", "Plant"))
        );
        await MigrateAsync(fine);
        // An overlay the projection cannot read: not one of the driver's deserialisation errors.
        await _factory
            .GetCollection<BsonDocument>(DomainModelCollections.ApplicationVersions)
            .UpdateOneAsync(
                new BsonDocument { { "entityId", bad }, { "isCurrent", true } },
                new BsonDocument(
                    "$set",
                    new BsonDocument(
                        "details.applicationOverseasSites.0.proposedChanges",
                        new BsonDocument("operationCodes", "not a list")
                    )
                ),
                cancellationToken: Ct
            );

        var report = await _reconciler.RunAsync(Ct);

        report.Unreadable.Should().Be(1);
        report.UnreadableByKind.Should().ContainKey(nameof(InvalidCastException));
        report.Matching.Should().Be(1);
        report.SampleApplicationIds.Should().Equal(bad);
    }

    [Fact]
    public async Task Run_Cancelled_StopsRatherThanCountingEveryApplicationAsUnreadable()
    {
        await MigrateAsync(await CreateAsync(Application(sites: Site(1, "001", "Plant"))));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var run = () => _reconciler.RunAsync(cancelled.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Run_WalksEveryBatchWithAPauseBetween()
    {
        for (var year = 2025; year < 2028; year++)
        {
            await CreateAsync(
                Application(ApplicationStatus.Started, Day1, year, Site(1, "001", "Plant"))
            );
        }

        await MigrateAsync(
            (
                await _factory
                    .GetCollection<BsonDocument>(LegacyCollection)
                    .Find(FilterDefinition<BsonDocument>.Empty)
                    .FirstAsync(Ct)
            )["_id"].AsObjectId
        );
        var reconciler = new ApplicationReconciler(
            _factory,
            _applications,
            new ApplicationReadModel(_stores),
            _stores.Conflicts
        )
        {
            BatchSize = 1,
            BatchPause = TimeSpan.FromMilliseconds(50),
        };

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var report = await reconciler.RunAsync(Ct);

        report.Applications.Should().Be(3);
        timer.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(140);
    }

    [Fact]
    public async Task ConflictsAreIndexedByApplication()
    {
        (await _stores.Conflicts.GetIndexNamesAsync(Ct)).Should().Contain("applicationIds");
    }

    [Fact]
    public async Task Run_AnApplicationNotYetMigrated_IsCountedNotCompared()
    {
        await CreateAsync(Application(sites: Site(1, "001", "Plant")));

        var report = await _reconciler.RunAsync(Ct);

        report.Applications.Should().Be(1);
        report.NotMigrated.Should().Be(1);
        report.Matching.Should().Be(0);
    }

    [Fact]
    public async Task Run_ALegacyDocumentThatCannotBeRead_IsCountedAndTheRunCarriesOn()
    {
        var unreadable = ObjectId.GenerateNewId();
        await _factory
            .GetCollection<BsonDocument>(LegacyCollection)
            .InsertOneAsync(
                new BsonDocument
                {
                    { "_id", unreadable },
                    { "organisationId", ReExOrganisationId },
                    { "applicationStatus", new BsonDocument("not", "a status") },
                },
                cancellationToken: Ct
            );
        var fine = await CreateAsync(Application(sites: Site(1, "001", "Plant")));
        await MigrateAsync(fine);

        var report = await _reconciler.RunAsync(Ct);

        report.Unreadable.Should().Be(1);
        report.Matching.Should().Be(1);
        report.SampleApplicationIds.Should().Equal(unreadable);
    }
}
