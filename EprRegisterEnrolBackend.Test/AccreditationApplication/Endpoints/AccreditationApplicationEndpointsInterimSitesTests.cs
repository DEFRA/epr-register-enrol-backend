using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-603. The per-interim-site routes: create, update, withdraw and restore. One write touches one
// interim site, which is what makes "adding, amending or withdrawing one must not affect the
// others" a property of the API rather than something each client has to get right.
//
// Kept out of the (already very large) AccreditationApplicationEndpointsTests.cs, same as
// RecyclingOperationsEndpointTests.cs and AccreditationNumberEndpointTests.cs.
public class AccreditationApplicationEndpointsInterimSitesTests
    : IClassFixture<AccreditationApplicationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApplicationEndpointsInterimSitesTests(
        AccreditationApplicationTestFactory factory
    )
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private void Reset()
    {
        _factory.FakePersistence.Clear();
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCaseWorkingAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCdpUploaderService.ClearSubstitute(ClearOptions.All);
        _factory.MockAuditPersistence.ClearSubstitute(ClearOptions.All);
    }

    private static InterimSiteModel Interim(
        int siteId,
        string siteName = "Interim",
        DateTime? createdAt = null,
        DateTime? removedAt = null,
        List<string>? operationCodes = null,
        string? siteNumber = null
    ) =>
        new()
        {
            SiteId = siteId,
            // The default is deliberately the LEGACY SN-000N form, which is what records stored
            // before RA-603's numbering change carry. Tests about the new 001-999 numbering pass
            // siteNumber explicitly.
            SiteNumber = siteNumber ?? $"SN-{siteId:D4}",
            Country = "France",
            SiteName = siteName,
            AddressLine1 = "1 Rue Example",
            TownOrCity = "Paris",
            ContactName = "Marie Curie",
            ContactEmail = "marie@example.com",
            ContactPhone = "0033111222333",
            OperationCodes = operationCodes ?? ["R12"],
            CreatedAt = createdAt,
            RemovedAt = removedAt,
        };

    private AccreditationApplicationModel Seed(
        ApplicationStatus status = ApplicationStatus.Saved,
        SectionStatus sectionStatus = SectionStatus.Completed,
        InterimSiteModel? legacyMirror = null,
        List<InterimSiteModel>? interimSites = null,
        string? registrationId = null
    )
    {
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-123",
            RegistrationId = registrationId,
            Year = 2026,
            MaterialType = MaterialType.Steel,
            ApplicationStatus = status,
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                SectionStatus = sectionStatus,
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 1,
                        SiteName = "Test Site",
                        OperationCodes = ["R4"],
                        InterimSite = legacyMirror,
                        InterimSites = interimSites ?? [],
                    },
                ],
            },
        };
        _factory.FakePersistence.Seed(app);
        return app;
    }

    /// <summary>
    /// Another application under the same registration - a previous year's, typically. Its
    /// interim sites are in scope for numbering even though no route in this test class touches
    /// it, which is what "registration-scoped" means.
    /// </summary>
    private void SeedSibling(string registrationId, params InterimSiteModel[] interimSites)
    {
        _factory.FakePersistence.Seed(
            new AccreditationApplicationModel
            {
                Id = ObjectId.GenerateNewId(),
                OrganisationId = "org-123",
                RegistrationId = registrationId,
                Year = 2025,
                MaterialType = MaterialType.Steel,
                ApplicationStatus = ApplicationStatus.Saved,
                OverseasSites = new AccreditationApplicationOverseasSites
                {
                    SectionStatus = SectionStatus.Completed,
                    Sites =
                    [
                        new OverseasSiteModel
                        {
                            SiteId = 1,
                            SiteName = "Prior Year Site",
                            OperationCodes = ["R4"],
                            InterimSites = [.. interimSites],
                        },
                    ],
                },
            }
        );
    }

    private static AddInterimSiteRequest ValidRequest() =>
        new()
        {
            Country = "France",
            SiteName = "Interim Recycling Site",
            AddressLine1 = "1 Rue Example",
            TownOrCity = "Paris",
            ContactName = "Jane Smith",
            ContactEmail = "jane.smith@example.com",
            ContactPhone = "+33 1 23 45 67 89",
            OperationCodes = ["R12"],
        };

    private static string Collection(AccreditationApplicationModel app) =>
        $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/interim-sites";

    private static string Item(AccreditationApplicationModel app, int interimSiteId) =>
        $"{Collection(app)}/{interimSiteId}";

    private async Task<OverseasSiteModel> StoredSiteAsync(AccreditationApplicationModel app)
    {
        var stored = await _factory.FakePersistence.GetByIdAsync(
            app.OrganisationId,
            app.Id!.Value.ToString()
        );
        return stored!.OverseasSites!.Sites.Single(s => s.SiteId == 1);
    }

    // ── create ───────────────────────────────────────────────────────────────

    // AC01/AC06: the whole point. AddInterimSite used to 409 the moment an ORS had one.
    [Fact]
    public async Task Create_SecondInterimSiteOnTheSameOrs_Returns201AndKeepsTheFirst()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2, "First Depot")]);

        var response = await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var site = await StoredSiteAsync(app);
        site.InterimSites.Should().HaveCount(2);
        site.InterimSites.Should().Contain(i => i.SiteName == "First Depot");
    }

    [Fact]
    public async Task Create_AllocatesADistinctSiteIdAndSiteNumber()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2), Interim(3)]);

        await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        var site = await StoredSiteAsync(app);
        var created = site.InterimSites.Single(i => i.SiteName == "Interim Recycling Site");
        created.SiteId.Should().Be(4);
        // SiteId still counts on the shared ORS+interim sequence; siteNumber no longer does.
        // The two fixtures carry legacy SN-000N numbers, which do not parse, so this is 001.
        created.SiteNumber.Should().Be("001");
    }

    // A withdrawn interim site keeps its id forever, so the allocator has to keep counting past it.
    [Fact]
    public async Task Create_WithdrawnSitesStillClaimTheirIds()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2, removedAt: DateTime.UtcNow), Interim(3)]);

        await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        var site = await StoredSiteAsync(app);
        site.InterimSites.Select(i => i.SiteId).Should().OnlyHaveUniqueItems();
        site.InterimSites.Should().Contain(i => i.SiteId == 4);
    }

    [Fact]
    public async Task Create_StampsCreatedAtAndLeavesRemovedAtNull()
    {
        Reset();
        var app = Seed();

        await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        var created = (await StoredSiteAsync(app)).InterimSites.Single();
        created.CreatedAt.Should().NotBeNull();
        created.RemovedAt.Should().BeNull();
    }

    [Fact]
    public async Task Create_PointsTheLegacyMirrorAtTheFirstActiveSite()
    {
        Reset();
        var app = Seed();

        await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        var site = await StoredSiteAsync(app);
        site.InterimSite!.SiteName.Should().Be("Interim Recycling Site");
    }

    // RA-486's rule is unchanged and the new route reuses the same validator.
    [Theory]
    [InlineData(new string[] { }, HttpStatusCode.BadRequest)]
    [InlineData(new[] { "R3" }, HttpStatusCode.BadRequest)]
    [InlineData(new[] { "R12" }, HttpStatusCode.Created)]
    [InlineData(new[] { "R13" }, HttpStatusCode.Created)]
    public async Task Create_KeepsTheMandatoryInterimCodeRule(
        string[] operationCodes,
        HttpStatusCode expected
    )
    {
        Reset();
        var app = Seed();

        var response = await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest() with
            {
                OperationCodes = [.. operationCodes],
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task Create_UnknownOverseasSite_Returns404()
    {
        Reset();
        var app = Seed();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/99/interim-sites",
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // The legacy singular route stays for callers that have not moved, but its duplicate guard is
    // gone - it is now just another way to append.
    [Fact]
    public async Task LegacySingularRoute_NoLongerRejectsASecondInterimSite()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2)]);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/interim-site",
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await StoredSiteAsync(app)).InterimSites.Should().HaveCount(2);
    }

    // ── update ───────────────────────────────────────────────────────────────

    // AC04: amending one must not touch the others.
    [Fact]
    public async Task Update_ChangesOnlyTheTargetedInterimSite()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2, "First"), Interim(3, "Second")]);

        var response = await _client.PatchAsJsonAsync(
            Item(app, 3),
            ValidRequest() with
            {
                SiteName = "Second Renamed",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var site = await StoredSiteAsync(app);
        site.InterimSites.Single(i => i.SiteId == 3).SiteName.Should().Be("Second Renamed");
        site.InterimSites.Single(i => i.SiteId == 2).SiteName.Should().Be("First");
    }

    [Fact]
    public async Task Update_KeepsTheServerOwnedIdentityAndDates()
    {
        Reset();
        var createdAt = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
        var app = Seed(interimSites: [Interim(2, createdAt: createdAt)]);

        await _client.PatchAsJsonAsync(
            Item(app, 2),
            ValidRequest() with
            {
                SiteName = "Renamed",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        var updated = (await StoredSiteAsync(app)).InterimSites.Single();
        updated.SiteId.Should().Be(2);
        updated.SiteNumber.Should().Be("SN-0002");
        updated.CreatedAt.Should().Be(createdAt);
        updated.RemovedAt.Should().BeNull();
    }

    // Editing something the operator has withdrawn is a mistake, not a silent resurrection.
    [Fact]
    public async Task Update_WithdrawnInterimSite_Returns409()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2, removedAt: DateTime.UtcNow)]);

        var response = await _client.PatchAsJsonAsync(
            Item(app, 2),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Update_UnknownInterimSite_Returns404()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2)]);

        var response = await _client.PatchAsJsonAsync(
            Item(app, 99),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── withdraw ─────────────────────────────────────────────────────────────

    // AC05: no longer visible, but still in the database.
    [Fact]
    public async Task Withdraw_StampsRemovedAtAndKeepsTheRecord()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2)]);

        var response = await _client.DeleteAsync(
            Item(app, 2),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var site = await StoredSiteAsync(app);
        site.InterimSites.Should().ContainSingle();
        site.InterimSites[0].RemovedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Withdraw_LeavesTheOtherInterimSitesAndTheirCodesUnchanged()
    {
        Reset();
        var app = Seed(
            interimSites: [Interim(2, "First"), Interim(3, "Second", operationCodes: ["R13"])]
        );

        await _client.DeleteAsync(Item(app, 2), TestContext.Current.CancellationToken);

        var survivor = (await StoredSiteAsync(app)).InterimSites.Single(i => i.SiteId == 3);
        survivor.RemovedAt.Should().BeNull();
        survivor.SiteName.Should().Be("Second");
        survivor.OperationCodes.Should().Equal("R13");
    }

    [Fact]
    public async Task Withdraw_RepointsTheLegacyMirrorAtTheNextActiveSite()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2, "First"), Interim(3, "Second")]);

        await _client.DeleteAsync(Item(app, 2), TestContext.Current.CancellationToken);

        (await StoredSiteAsync(app)).InterimSite!.SiteId.Should().Be(3);
    }

    [Fact]
    public async Task Withdraw_TheLastActiveSite_ClearsTheLegacyMirror()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2)]);

        await _client.DeleteAsync(Item(app, 2), TestContext.Current.CancellationToken);

        (await StoredSiteAsync(app)).InterimSite.Should().BeNull();
    }

    [Fact]
    public async Task Withdraw_AlreadyWithdrawn_IsIdempotentAndKeepsTheOriginalDate()
    {
        Reset();
        var withdrawnAt = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var app = Seed(interimSites: [Interim(2, removedAt: withdrawnAt)]);

        var response = await _client.DeleteAsync(
            Item(app, 2),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredSiteAsync(app)).InterimSites[0].RemovedAt.Should().Be(withdrawnAt);
    }

    [Fact]
    public async Task Withdraw_UnknownInterimSite_Returns404()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2)]);

        var response = await _client.DeleteAsync(
            Item(app, 99),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── restore ──────────────────────────────────────────────────────────────

    // Restore is in place: clearing RemovedAt is the whole operation, so the record that comes back
    // is the one that went away rather than a lookalike with a new number.
    [Fact]
    public async Task Restore_ClearsRemovedAtAndKeepsTheOriginalIdentity()
    {
        Reset();
        var createdAt = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
        var app = Seed(
            interimSites: [Interim(2, createdAt: createdAt, removedAt: DateTime.UtcNow)]
        );

        var response = await _client.PostAsync(
            $"{Item(app, 2)}/restore",
            content: null,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var restored = (await StoredSiteAsync(app)).InterimSites.Single();
        restored.RemovedAt.Should().BeNull();
        restored.SiteId.Should().Be(2);
        restored.SiteNumber.Should().Be("SN-0002");
        restored.CreatedAt.Should().Be(createdAt);
    }

    [Fact]
    public async Task Restore_BringsTheSiteBackIntoTheLegacyMirror()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2, removedAt: DateTime.UtcNow)]);

        await _client.PostAsync(
            $"{Item(app, 2)}/restore",
            content: null,
            TestContext.Current.CancellationToken
        );

        (await StoredSiteAsync(app)).InterimSite!.SiteId.Should().Be(2);
    }

    [Fact]
    public async Task Restore_AnActiveSite_IsIdempotent()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2)]);

        var response = await _client.PostAsync(
            $"{Item(app, 2)}/restore",
            content: null,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredSiteAsync(app)).InterimSites.Single().RemovedAt.Should().BeNull();
    }

    [Fact]
    public async Task Restore_UnknownInterimSite_Returns404()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2)]);

        var response = await _client.PostAsync(
            $"{Item(app, 99)}/restore",
            content: null,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── guards shared with the existing routes ───────────────────────────────

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task EveryRoute_TerminalApplication_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = Seed(status: status, interimSites: [Interim(2)]);

        var create = await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var update = await _client.PatchAsJsonAsync(
            Item(app, 2),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var withdraw = await _client.DeleteAsync(
            Item(app, 2),
            TestContext.Current.CancellationToken
        );
        var restore = await _client.PostAsync(
            $"{Item(app, 2)}/restore",
            content: null,
            TestContext.Current.CancellationToken
        );

        create.StatusCode.Should().Be(HttpStatusCode.Conflict);
        update.StatusCode.Should().Be(HttpStatusCode.Conflict);
        withdraw.StatusCode.Should().Be(HttpStatusCode.Conflict);
        restore.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // UpdateAsync is version-guarded, so a concurrent write to the application makes it return
    // null. That is a lost race, not a server fault: report it as 409 the way the create route's
    // exhausted retry does, not as a 500.
    [Theory]
    [InlineData("update")]
    [InlineData("withdraw")]
    [InlineData("restore")]
    public async Task LostVersionRace_Returns409(string route)
    {
        Reset();
        var app = Seed(
            interimSites: [Interim(2, removedAt: route == "restore" ? DateTime.UtcNow : null)]
        );
        _factory.FakePersistence.FailNextUpdate = true;
        var ct = TestContext.Current.CancellationToken;

        var response = route switch
        {
            "update" => await _client.PatchAsJsonAsync(Item(app, 2), ValidRequest(), ct),
            "withdraw" => await _client.DeleteAsync(Item(app, 2), ct),
            _ => await _client.PostAsync($"{Item(app, 2)}/restore", content: null, ct),
        };

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // -- AC12: a queried ORS section stays editable ---------------------------
    //
    // These pin behaviour that already existed rather than behaviour this ticket added.
    // IsSectionEditable is `!LockedStatuses.Contains(appStatus) || sectionStatus == Queried`, so a
    // section the regulator has queried stays editable even though the application as a whole is
    // locked - which is exactly what lets an operator answer the query.
    //
    // The risk AC12 actually carries is not that the rule is wrong, it is that one of the four new
    // routes quietly fails to apply it. They share ResolveEditableOverseasSiteAsync for that
    // reason, and these tests are what would notice if someone inlined the guard into one of them
    // and dropped the Queried clause.

    [Theory]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.DulyMade)]
    [InlineData(ApplicationStatus.Updated)]
    [InlineData(ApplicationStatus.AwaitingDecision)]
    [InlineData(ApplicationStatus.Queried)]
    public async Task QueriedOrsSection_OnALockedApplication_AllowsAddingAnInterimSite(
        ApplicationStatus lockedStatus
    )
    {
        Reset();
        var app = Seed(status: lockedStatus, sectionStatus: SectionStatus.Queried);

        var response = await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.DulyMade)]
    [InlineData(ApplicationStatus.Updated)]
    [InlineData(ApplicationStatus.AwaitingDecision)]
    [InlineData(ApplicationStatus.Queried)]
    public async Task QueriedOrsSection_OnALockedApplication_AllowsWithdrawingAnInterimSite(
        ApplicationStatus lockedStatus
    )
    {
        Reset();
        var app = Seed(
            status: lockedStatus,
            sectionStatus: SectionStatus.Queried,
            interimSites: [Interim(2)]
        );

        var response = await _client.DeleteAsync(
            Item(app, 2),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredSiteAsync(app)).InterimSites[0].RemovedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task QueriedOrsSection_OnALockedApplication_AllowsEditingAndRestoring()
    {
        Reset();
        var app = Seed(
            status: ApplicationStatus.Submitted,
            sectionStatus: SectionStatus.Queried,
            interimSites: [Interim(2), Interim(3, removedAt: DateTime.UtcNow)]
        );

        var update = await _client.PatchAsJsonAsync(
            Item(app, 2),
            ValidRequest() with
            {
                SiteName = "Renamed Under Query",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        var restore = await _client.PostAsync(
            $"{Item(app, 3)}/restore",
            content: null,
            TestContext.Current.CancellationToken
        );

        update.StatusCode.Should().Be(HttpStatusCode.OK);
        restore.StatusCode.Should().Be(HttpStatusCode.OK);
        var site = await StoredSiteAsync(app);
        site.InterimSites.Single(i => i.SiteId == 2).SiteName.Should().Be("Renamed Under Query");
        site.InterimSites.Single(i => i.SiteId == 3).RemovedAt.Should().BeNull();
    }

    // The other half of the rule, and the half that makes the tests above mean something: a locked
    // application whose ORS section is NOT the queried one stays shut.
    [Theory]
    [InlineData(SectionStatus.Completed)]
    [InlineData(SectionStatus.InProgress)]
    [InlineData(SectionStatus.NotStarted)]
    public async Task UnqueriedOrsSection_OnALockedApplication_RefusesEveryInterimSiteWrite(
        SectionStatus sectionStatus
    )
    {
        Reset();
        var app = Seed(
            status: ApplicationStatus.Submitted,
            sectionStatus: sectionStatus,
            interimSites: [Interim(2)]
        );

        var create = await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var update = await _client.PatchAsJsonAsync(
            Item(app, 2),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var withdraw = await _client.DeleteAsync(
            Item(app, 2),
            TestContext.Current.CancellationToken
        );
        var restore = await _client.PostAsync(
            $"{Item(app, 2)}/restore",
            content: null,
            TestContext.Current.CancellationToken
        );

        create.StatusCode.Should().Be(HttpStatusCode.Conflict);
        update.StatusCode.Should().Be(HttpStatusCode.Conflict);
        withdraw.StatusCode.Should().Be(HttpStatusCode.Conflict);
        restore.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // -- AC08: withdrawing the last interim site does not block submission ----
    //
    // RA-486 decoupled an ORS from its interim sites: an ORS needs at least one of R3/R4/R5 and
    // nothing at all is required about interim sites. AccreditationApplicationSections contains no
    // reference to them, so an ORS whose only interim site has been withdrawn should be exactly as
    // submittable as one that never had an interim site.
    //
    // That is a conclusion drawn from reading the code, which is the kind of thing that is true
    // until someone adds a completeness rule. This proves it end to end instead.

    private AccreditationApplicationModel SeedSubmittableExporter(
        List<InterimSiteModel> interimSites
    )
    {
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-123",
            Year = 2026,
            MaterialType = MaterialType.Steel,
            ApplicationStatus = ApplicationStatus.Started,
            IsExporter = true,
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                SectionStatus = SectionStatus.Completed,
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 1,
                        SiteName = "Test Site",
                        Selected = true,
                        OperationCodes = ["R4"],
                        InterimSites = interimSites,
                        InterimSite = interimSites.FirstOrDefault(i => i.RemovedAt is null),
                    },
                ],
            },
            BesEvidence = new AccreditationApplicationBesEvidence
            {
                SectionStatus = SectionStatus.Completed,
            },
        };
        app.Prns.SectionStatus = SectionStatus.Completed;
        app.BusinessPlan.SectionStatus = SectionStatus.Completed;
        app.SamplingPlan.SectionStatus = SectionStatus.Completed;

        _factory
            .MockCaseWorkingAdapter.SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromResult(new CaseWorkingSubmissionResult("RA-123456789", Guid.NewGuid()))
            );

        _factory.FakePersistence.Seed(app);
        return app;
    }

    private async Task<HttpResponseMessage> SubmitAsync(AccreditationApplicationModel app) =>
        await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            new SubmitRequest
            {
                FullName = "John",
                JobTitle = "Manager",
                Email = "j@x.com",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

    [Fact]
    public async Task Submit_OrsWhoseOnlyInterimSiteIsWithdrawn_StillSucceeds()
    {
        Reset();
        var app = SeedSubmittableExporter([Interim(2, removedAt: DateTime.UtcNow)]);

        var response = await SubmitAsync(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Submit_OrsWithNoInterimSitesAtAll_StillSucceeds()
    {
        Reset();
        var app = SeedSubmittableExporter([]);

        var response = await SubmitAsync(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Submit_OrsWithSeveralInterimSites_StillSucceeds()
    {
        Reset();
        var app = SeedSubmittableExporter(
            [Interim(2), Interim(3), Interim(4, removedAt: DateTime.UtcNow)]
        );

        var response = await SubmitAsync(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Withdrawing the last one through the real route, then submitting - the sequence an operator
    // would actually perform, rather than a document hand-built into that state.
    [Fact]
    public async Task WithdrawingTheLastInterimSiteThenSubmitting_Succeeds()
    {
        Reset();
        var app = SeedSubmittableExporter([Interim(2)]);

        var withdraw = await _client.DeleteAsync(
            Item(app, 2),
            TestContext.Current.CancellationToken
        );
        withdraw.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await SubmitAsync(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── site numbers are their own 001-999 set ───────────────────────────────
    //
    // An interim site's siteNumber is a real-world identifier a regulator reads. It runs 001-999
    // as its OWN set, independent of the ORS ids, and - like them - it is scoped by
    // RegistrationId, so it stays unique across every year of a company's history rather than
    // restarting each application.
    //
    // It used to be derived from NextSiteId, the shared ORS+interim internal counter. That made
    // interim numbers skip whenever an ORS was added: an accreditation's two interim sites came
    // out SN-0002 and SN-0004 rather than 001 and 002.
    //
    // SiteId keeps the shared sequence and is unaffected. That is what the routes address, and it
    // is why an interim site can be named without also naming its parent.

    [Fact]
    public async Task Create_FirstInterimSiteUnderARegistration_IsNumbered001()
    {
        Reset();
        var app = Seed(registrationId: "reg-1");

        await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        (await StoredSiteAsync(app)).InterimSites.Single().SiteNumber.Should().Be("001");
    }

    [Fact]
    public async Task Create_NumbersInterimSitesSequentially()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2, siteNumber: "001")], registrationId: "reg-1");

        await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        (await StoredSiteAsync(app))
            .InterimSites.Select(i => i.SiteNumber)
            .Should()
            .Equal("001", "002");
    }

    // The defect this fixes. An ORS added in between used to consume a number from the interim
    // sequence, because both came off the same counter.
    [Fact]
    public async Task Create_InterimNumbersDoNotSkipWhenOverseasSitesAreAdded()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2, siteNumber: "001")], registrationId: "reg-1");
        app.OverseasSites!.Sites.Add(
            new OverseasSiteModel
            {
                SiteId = 50,
                SiteName = "Another ORS",
                OperationCodes = ["R4"],
            }
        );

        await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        (await StoredSiteAsync(app)).InterimSites.Should().Contain(i => i.SiteNumber == "002");
    }

    // Counted across every ORS on the application, not restarted per ORS.
    [Fact]
    public async Task Create_NumbersAreUniqueAcrossEveryOverseasSite()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2, siteNumber: "001")], registrationId: "reg-1");
        app.OverseasSites!.Sites.Add(
            new OverseasSiteModel
            {
                SiteId = 50,
                SiteName = "Another ORS",
                OperationCodes = ["R4"],
                InterimSites = [Interim(51, siteNumber: "002")],
            }
        );

        await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        var stored = await _factory.FakePersistence.GetByIdAsync(
            app.OrganisationId,
            app.Id!.Value.ToString()
        );
        var allNumbers = stored!
            .OverseasSites!.Sites.SelectMany(site => site.InterimSites)
            .Select(i => i.SiteNumber)
            .ToList();
        allNumbers.Should().OnlyHaveUniqueItems();
        allNumbers.Should().Contain("003");
    }

    // A withdrawn interim site keeps its number for as long as the record lasts, which under AC05
    // is permanently. Reissuing it would put a regulator-visible identifier on two records.
    [Fact]
    public async Task Create_WithdrawnInterimSitesKeepTheirNumbersReserved()
    {
        Reset();
        var app = Seed(
            interimSites:
            [
                Interim(2, siteNumber: "001", removedAt: DateTime.UtcNow),
                Interim(3, siteNumber: "002"),
            ],
            registrationId: "reg-1"
        );

        await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        (await StoredSiteAsync(app))
            .InterimSites.Select(i => i.SiteNumber)
            .Should()
            .Equal("001", "002", "003");
    }

    // Records stored before this change carry SN-000N, which is not a number. Numbering has to
    // keep working rather than trip over them or, worse, parse them into something.
    [Fact]
    public async Task Create_IgnoresLegacySiteNumbers()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2, siteNumber: "SN-0002")], registrationId: "reg-1");

        await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        (await StoredSiteAsync(app)).InterimSites.Should().Contain(i => i.SiteNumber == "001");
    }

    // The scope rule, and the reason it is not per-application: a registration spans years, and a
    // number reissued next year would collide with one already in a submitted return.
    [Fact]
    public async Task Create_ContinuesFromOtherApplicationsUnderTheSameRegistration()
    {
        Reset();
        var app = Seed(registrationId: "reg-1");
        SeedSibling("reg-1", Interim(90, siteNumber: "001"), Interim(91, siteNumber: "002"));

        await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        (await StoredSiteAsync(app)).InterimSites.Single().SiteNumber.Should().Be("003");
    }

    // Scoped, not global. Another company's numbers are none of this registration's business.
    [Fact]
    public async Task Create_IgnoresInterimSitesUnderADifferentRegistration()
    {
        Reset();
        var app = Seed(registrationId: "reg-1");
        SeedSibling("reg-OTHER", Interim(90, siteNumber: "500"));

        await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        (await StoredSiteAsync(app)).InterimSites.Single().SiteNumber.Should().Be("001");
    }

    // No registration established yet means no prior-year history to collide with, so the
    // application's own sites are the whole scope. Self-corrects once a real id is assigned.
    [Fact]
    public async Task Create_NoRegistrationId_NumbersWithinTheApplicationAlone()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2, siteNumber: "001")], registrationId: null);
        SeedSibling("reg-1", Interim(90, siteNumber: "700"));

        await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        (await StoredSiteAsync(app)).InterimSites.Should().Contain(i => i.SiteNumber == "002");
    }

    // Two writers under one registration must not mint the same number. Same guard and same
    // bounded retry as AddOverseasSite (RA-482), driven here without real concurrency.
    [Fact]
    public async Task Create_RetriesWhenAnotherWriterClaimsTheNumberFirst()
    {
        Reset();
        var app = Seed(registrationId: "reg-1");
        _factory.FakePersistence.FailNextInterimSiteNumberWrites = 1;

        var response = await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await StoredSiteAsync(app)).InterimSites.Should().HaveCount(1);
    }

    // Bounded, not unlimited - a genuinely stuck conflict fails loudly rather than spinning.
    [Fact]
    public async Task Create_GivesUpAfterThreeConflictingAttempts()
    {
        Reset();
        var app = Seed(registrationId: "reg-1");
        _factory.FakePersistence.FailNextInterimSiteNumberWrites = 3;

        var response = await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        // 409, matching AddOverseasSite's give-up on the same situation.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // RA-603 review (Aysha): interim site ids are unique application-wide, but the bulk PATCH
    // body is client-shaped and could list one under two overseas sites. Merge would then keep
    // it active under one parent and stamp it withdrawn under the other - the same record stored
    // twice. Refused up front instead.
    [Fact]
    public async Task BulkPatch_SameInterimSiteUnderTwoOverseasSites_IsRejected()
    {
        Reset();
        var app = Seed(
            status: ApplicationStatus.Started,
            sectionStatus: SectionStatus.InProgress,
            interimSites: [Interim(42)]
        );
        var request = new PatchOverseasSitesRequest
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    SiteName = "Test Site",
                    OperationCodes = ["R4"],
                    InterimSites = [Interim(42)],
                },
                new OverseasSiteModel
                {
                    SiteId = 2,
                    SiteName = "Second Site",
                    OperationCodes = ["R4"],
                    InterimSites = [Interim(42)],
                },
            ],
        };

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            JsonOptions,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredSiteAsync(app)).InterimSites.Should().ContainSingle(i => i.SiteId == 42);
    }

    // RA-603 review (Aysha): the same id twice under one overseas site is the same problem -
    // both copies would be stored and a later withdraw would stamp only the first.
    [Fact]
    public async Task BulkPatch_SameInterimSiteTwiceUnderOneOverseasSite_IsRejected()
    {
        Reset();
        var app = Seed(
            status: ApplicationStatus.Started,
            sectionStatus: SectionStatus.InProgress,
            interimSites: [Interim(42)]
        );
        var request = new PatchOverseasSitesRequest
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    SiteName = "Test Site",
                    OperationCodes = ["R4"],
                    InterimSites = [Interim(42), Interim(42)],
                },
            ],
        };

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            JsonOptions,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredSiteAsync(app)).InterimSites.Should().ContainSingle(i => i.SiteId == 42);
    }

    // RA-603 review (Aysha): the write that lost can have lost to a status change rather than a
    // number clash - the guard only compares Version. The retry must re-check that the
    // application can still be edited, not just re-read it.
    [Fact]
    public async Task Create_RetryAfterTheApplicationWasWithdrawn_IsRefused()
    {
        Reset();
        var app = Seed(status: ApplicationStatus.Started, sectionStatus: SectionStatus.InProgress);
        _factory.FakePersistence.FailNextInterimSiteNumberWrites = 1;
        _factory.FakePersistence.OnLostRace = stored =>
            stored.ApplicationStatus = ApplicationStatus.Withdrawn;

        var response = await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StoredSiteAsync(app)).InterimSites.Should().BeEmpty();
    }

    // RA-603 review (Aysha): a retry re-reads the document, and the re-read can hold a
    // pre-RA-603 ORS whose only interim site is still in the singular field. Appending to the
    // list without normalising first would drop that site. Pins the explicit normalise on the
    // retry path, so it no longer depends on NextSiteId doing it as a side effect.
    [Fact]
    public async Task Create_RetryAgainstALegacyOnlyDocument_KeepsTheLegacyInterimSite()
    {
        Reset();
        var app = Seed(status: ApplicationStatus.Started, sectionStatus: SectionStatus.InProgress);
        _factory.FakePersistence.FailNextInterimSiteNumberWrites = 1;
        _factory.FakePersistence.OnLostRace = stored =>
            stored.OverseasSites = new AccreditationApplicationOverseasSites
            {
                SectionStatus = SectionStatus.InProgress,
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 1,
                        SiteName = "Test Site",
                        OperationCodes = ["R4"],
                        InterimSite = Interim(7, "Legacy Depot"),
                        InterimSites = [],
                    },
                ],
            };

        var response = await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var site = await StoredSiteAsync(app);
        site.InterimSites.Should().HaveCount(2);
        site.InterimSites.Should().Contain(i => i.SiteId == 7 && i.SiteName == "Legacy Depot");
    }

    // The format's ceiling. 999 is the last one the three-digit form can express.
    [Fact]
    public async Task Create_AtCapacity_Returns422AndAddsNothing()
    {
        Reset();
        var app = Seed(interimSites: [Interim(2, siteNumber: "999")], registrationId: "reg-1");

        var response = await _client.PostAsJsonAsync(
            Collection(app),
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await StoredSiteAsync(app)).InterimSites.Should().HaveCount(1);
    }
}
