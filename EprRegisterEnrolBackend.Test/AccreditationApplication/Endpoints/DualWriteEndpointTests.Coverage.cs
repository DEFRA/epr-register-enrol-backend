using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using EprRegisterEnrolBackend.DomainModel.Seeding;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// RA-705 characterization tests for the dual-write paths the coverage audit found thin: what the model holds
/// after a write (not just its version), rollback on the routes that had none, and the first touch of a group
/// the seed has already stamped. They pin behaviour the generic unit of work should already give.
/// </summary>
public sealed partial class DualWriteEndpointTests
{
    private static readonly Dictionary<string, string?> s_seedAndDualWriteOn = new()
    {
        ["DomainModel:ExpandEnabled"] = "true",
        ["DomainModel:DualWrite"] = "true",
        ["DomainModel:DualWriteSeed"] = "true",
    };

    private static readonly Dictionary<string, string?> s_seedOnly = new()
    {
        ["DomainModel:ExpandEnabled"] = "true",
        ["DomainModel:DualWriteSeed"] = "true",
    };

    // --- helpers --------------------------------------------------------------------------------------------------

    // The application as the model alone would serve it, the shape the reads will switch to.
    private async Task<AccreditationApplicationModel> ProjectedAsync(
        AccreditationApplicationModel application
    )
    {
        using var session = await StartSessionAsync();
        var projected = await new ApplicationReadModel(Stores).GetAsync(
            application.Id!.Value,
            session,
            Ct
        );
        return Assert.IsType<AccreditationApplicationModel>(projected);
    }

    private async Task<IClientSessionHandle> StartSessionAsync() =>
        await _factory
            .Services.GetRequiredService<IMongoDbClientFactory>()
            .GetClient()
            .StartSessionAsync(cancellationToken: Ct);

    private static string Json<T>(T? value)
        where T : class => value is null ? "null" : value.ToBsonDocument().ToJson();

    // The sections the section routes write, as the model holds them against as legacy holds them.
    private static void AssertSectionsMatch(
        AccreditationApplicationModel legacy,
        AccreditationApplicationModel projected
    )
    {
        Assert.Equal(Json(legacy.Prns), Json(projected.Prns));
        Assert.Equal(Json(legacy.BusinessPlan), Json(projected.BusinessPlan));
        Assert.Equal(Json(legacy.SamplingPlan), Json(projected.SamplingPlan));
        Assert.Equal(Json(legacy.BesEvidence), Json(projected.BesEvidence));
        Assert.Equal(
            legacy.OverseasSites?.Sites.Select(s => Json(s.BesEvidence)),
            projected.OverseasSites?.Sites.Select(s => Json(s.BesEvidence))
        );
    }

    // Runs the real sync, then fails: everything the sync wrote is in the transaction when it is aborted.
    private sealed class FailsAfterSync(IApplicationModelSync inner) : IApplicationModelSync
    {
        public async Task SyncAsync(
            ObjectId applicationId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default
        )
        {
            await inner.SyncAsync(applicationId, session, cancellationToken);
            throw new InvalidOperationException("model write failed after the sync");
        }
    }

    // --- gap 2: approval rolls back on a model failure ------------------------------------------------------------

    [Fact]
    public async Task StatusPush_Approved_ModelWriteFails_RollsBackTheLegacyWriteAndCreatesNoAccreditation()
    {
        FailModelWrites();
        var (application, _) = await ReadyToApproveAsync();

        var response = await StatusPushAsync(application, "approved");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal(ApplicationStatus.Submitted, legacy.ApplicationStatus);
        Assert.Null(legacy.CaseManagementStatusUpdatedAt);
        Assert.Null(legacy.AccreditationEntityId);
        Assert.Empty(await CurrentAccreditationsAsync());
        Assert.Empty(await CurrentSitesAsync());
    }

    [Fact]
    public async Task StatusPush_Approved_ModelWriteFailsAfterTheSync_RollsBackTheAccreditationAndTheSites()
    {
        // The sync has already inserted the Accreditation and activated the sites when the transaction aborts.
        Restart(
            s_dualWriteOn,
            services =>
                services.Replace(
                    ServiceDescriptor.Singleton<IApplicationModelSync>(sp => new FailsAfterSync(
                        ActivatorUtilities.CreateInstance<ApplicationModelSync>(sp)
                    ))
                )
        );
        var (application, _) = await ReadyToApproveAsync();

        var response = await StatusPushAsync(application, "approved");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal(ApplicationStatus.Submitted, legacy.ApplicationStatus);
        Assert.Null(legacy.AccreditationEntityId);
        Assert.Null(legacy.RegistrationEntityId);
        Assert.Empty(await CurrentAccreditationsAsync());
        Assert.Empty(await CurrentSitesAsync());
        Assert.Null(await CurrentApplicationAsync(application));
    }

    // --- gap 3: an accreditation number reused from the previous accreditation ------------------------------------

    [Fact]
    public async Task AccreditationNumber_ReusedFromPreviousAccreditation_DualWriteOn_ReservesOnTheApplicationVersion()
    {
        // Distinct tails, so the reserved number says which source it was derived from: the previous
        // accreditation's ...0009, not the registration's ...0001.
        var previous = await NumberedApplicationAsync(
            ApplicationStatus.Approved,
            2026,
            "R26ER5000270001ST"
        );
        previous.AccreditationReference = "A26ER5000270009ST";
        await Applications.UpdateAsync(previous);
        var application = await NumberedApplicationAsync(
            ApplicationStatus.Started,
            2027,
            "R26ER5000270001ST"
        );

        var response = await _client.PostAsync(NumberUrl(application, "accreditation"), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("A27ER5000270009ST", (await LegacyAsync(application))!.AccreditationReference);
        var entity = (await CurrentApplicationAsync(application))!;
        Assert.Equal("A27ER5000270009ST", entity.AsAccreditation().ReservedAccreditationNumber);
        Assert.Equal(ChangeReason.ApplicationEdited, entity.LastChange.Reason);
        Assert.Equal(
            "A27ER5000270009ST",
            (await ProjectedAsync(application)).AccreditationReference
        );
    }

    // --- gap 4: what Submit writes to the model -------------------------------------------------------------------

    [Fact]
    public async Task Submit_WritesTheSubmitterDateSentPaymentReferenceAndOrgIdToTheModel()
    {
        CaseManagementAcceptsSubmissions(Guid.NewGuid());
        _reEx
            .GetOrganisationNumberAsync(OrganisationId, Arg.Any<CancellationToken>())
            .Returns(ReExResult<int?>.Success(500027, 200));
        var application = await ReadyToSubmitAsync();
        Assert.Null(application.OrgId);

        var response = await SubmitAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal(500027, legacy.OrgId);
        var entity = (await CurrentApplicationAsync(application))!;
        Assert.Equal("Ann Smith", entity.SubmittedBy!.FullName);
        Assert.Equal("Director", entity.SubmittedBy.JobTitle);
        Assert.Equal("ann@example.com", entity.SubmittedBy.Email);
        Assert.Equal("PAY-1", entity.PaymentReference);
        Assert.NotNull(legacy.DateSent);
        Assert.Equal(legacy.DateSent.Value, entity.DateSent!.Value, TimeSpan.FromMilliseconds(1));
        var projected = await ProjectedAsync(application);
        Assert.Equal(500027, projected.OrgId);
        Assert.Equal("Ann Smith", projected.SubmittedBy!.FullName);
        Assert.Equal("PAY-1", projected.PaymentReference);
        Assert.Equal(legacy.DateSent.Value, projected.DateSent!.Value, TimeSpan.FromMilliseconds(1));
    }

    // --- gap 5: the query and its resubmission --------------------------------------------------------------------

    [Fact]
    public async Task QueryPush_WritesTheQueryNoteAndSectionKeysToTheModel()
    {
        var (application, _) = await SubmittedWithProposedSiteAsync();

        var response = await QueryPushAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entity = (await CurrentApplicationAsync(application))!;
        Assert.Equal("Please check the business plan.", entity.Query!.QueryNote);
        Assert.Equal(["business-plan"], entity.Query.QueriedSectionKeys);
        var projected = await ProjectedAsync(application);
        Assert.Equal(ApplicationStatus.Queried, projected.ApplicationStatus);
        Assert.Equal(SectionStatus.Queried, projected.BusinessPlan.SectionStatus);
        Assert.Equal("Please check the business plan.", projected.Query!.QueryNote);
        Assert.Equal(["business-plan"], projected.Query.QueriedSectionKeys);
    }

    [Fact]
    public async Task Resubmit_ClearsTheQueriedSectionKeysAndRecordsTheSubmissionOnTheModel()
    {
        var application = await QueriedAsync();

        var response = await ResubmitAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Empty(legacy.Query!.QueriedSectionKeys);
        var entity = (await CurrentApplicationAsync(application))!;
        Assert.Empty(entity.Query!.QueriedSectionKeys);
        var submission = Assert.Single(entity.Query.QuerySubmissions);
        Assert.Equal(["business-plan"], submission.SectionKeys);
        Assert.Equal("Ann Smith", submission.QuerySubmitterContactDetails.FullName);
        var projected = await ProjectedAsync(application);
        Assert.Equal(ApplicationStatus.Updated, projected.ApplicationStatus);
        Assert.NotEqual(SectionStatus.Queried, projected.BusinessPlan.SectionStatus);
        Assert.Equal(legacy.BusinessPlan.SectionStatus, projected.BusinessPlan.SectionStatus);
        Assert.Equal(Json(legacy.Query), Json(projected.Query));
    }

    // --- gap 6: section content on the model ----------------------------------------------------------------------

    private async Task<(
        AccreditationApplicationModel Legacy,
        AccreditationApplicationModel Projected
    )> PatchSectionAsync(string path, string body)
    {
        var application = await ApplicationAsync(null, ReExSite());

        var response = await _client.PatchAsync(
            AppUrl(application, path),
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            Ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        var projected = await ProjectedAsync(application);
        AssertSectionsMatch(legacy, projected);
        return (legacy, projected);
    }

    // The marker is text the section's content must hold (enums are stored as numbers, so they are asserted by
    // type in the tests below instead).
    [Theory]
    [InlineData(
        "business-plan",
        """{"newInfrastructurePercent":10,"newInfrastructureDetail":"A new baler"}""",
        "A new baler"
    )]
    [InlineData(
        "sampling-plan",
        """{"files":[{"fileId":"f-9","filename":"plan9.pdf","contentType":"application/pdf","uploadedByUserId":"user-1","s3Key":"plans/plan9.pdf"}]}""",
        "plan9.pdf"
    )]
    public async Task SectionPatch_ModelHoldsTheSameSectionContentAsLegacy(
        string path,
        string body,
        string marker
    )
    {
        var (legacy, projected) = await PatchSectionAsync(path, body);

        Assert.Contains(marker, Json(legacy));
        Assert.Contains(marker, Json(projected));
    }

    [Theory]
    [InlineData("prns")]
    [InlineData("tonnage")]
    public async Task PrnsPatch_ModelHoldsThePlannedTonnageBand(string path)
    {
        var (_, projected) = await PatchSectionAsync(
            path,
            """{"plannedTonnageBand":"UpTo500","sectionStatus":"InProgress"}"""
        );

        Assert.Equal(PlannedTonnageBand.UpTo500, projected.Prns.PlannedTonnageBand);
        Assert.Equal(SectionStatus.InProgress, projected.Prns.SectionStatus);
    }

    [Fact]
    public async Task PatchBesEvidence_ModelHoldsTheSiteChoice()
    {
        var (_, projected) = await PatchSectionAsync(
            "overseas-sites/1/bes-evidence",
            """{"doYouWantToUploadMoreEvidence":true}"""
        );

        Assert.True(projected.OverseasSites!.Sites[0].BesEvidence!.DoYouWantToUploadMoreEvidence);
    }

    [Fact]
    public async Task PatchBesEvidenceSection_ModelHoldsTheSectionStatus()
    {
        var (_, projected) = await PatchSectionAsync(
            "bes-evidence",
            """{"sectionStatus":"InProgress"}"""
        );

        Assert.Equal(SectionStatus.InProgress, projected.BesEvidence!.SectionStatus);
    }

    [Fact]
    public async Task AddFile_ModelHoldsTheSamplingPlanFile()
    {
        UseScannedUpload("file-1", "plan.pdf");
        var application = await ApplicationAsync(null, ReExSite());

        var response = await _client.PostAsJsonAsync(
            AppUrl(application, "files"),
            new { fileUploadId = "upload-1" },
            Ct
        );

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        var projected = await ProjectedAsync(application);
        var file = Assert.Single(projected.SamplingPlan.Files);
        Assert.Equal("file-1", file.FileId);
        Assert.Equal("plan.pdf", file.Filename);
        AssertSectionsMatch((await LegacyAsync(application))!, projected);
    }

    [Fact]
    public async Task DeleteFile_ModelNoLongerHoldsTheSamplingPlanFile()
    {
        var application = await ApplicationAsync(null, ReExSite());
        application.SamplingPlan.Files =
        [
            new AccreditationApplicationFile
            {
                FileId = "file-1",
                Filename = "plan.pdf",
                ContentType = "application/pdf",
                UploadedByUserId = "user-1",
                S3Key = "plans/plan.pdf",
            },
        ];
        application = (await Applications.UpdateAsync(application))!;

        var response = await _client.DeleteAsync(AppUrl(application, "files/file-1"), Ct);

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        var projected = await ProjectedAsync(application);
        Assert.Empty(projected.SamplingPlan.Files);
        AssertSectionsMatch((await LegacyAsync(application))!, projected);
    }

    [Fact]
    public async Task AddBesEvidenceFile_ModelHoldsTheUploadedFile()
    {
        UseScannedUpload("bes-1", "permit.pdf");
        var application = await ApplicationAsync(null, ReExSite());

        var response = await _client.PostAsJsonAsync(
            AppUrl(application, "overseas-sites/1/bes-evidence/files"),
            new { fileUploadId = "upload-1" },
            Ct
        );

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        var projected = await ProjectedAsync(application);
        var file = Assert.Single(projected.OverseasSites!.Sites[0].BesEvidence!.BesEvidenceUploads);
        Assert.Equal("bes-1", file.FileId);
        Assert.Equal("permit.pdf", file.Filename);
        AssertSectionsMatch((await LegacyAsync(application))!, projected);
    }

    [Fact]
    public async Task PatchBesEvidenceFile_ModelHoldsTheNewExpiryDate()
    {
        var application = await WithBesEvidenceFileAsync();

        var response = await _client.PatchAsJsonAsync(
            AppUrl(application, "overseas-sites/1/bes-evidence/files/bes-1"),
            new { besEvidenceExpiryDate = "2028-01-31" },
            Ct
        );

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        var projected = await ProjectedAsync(application);
        Assert.Equal(
            "2028-01-31",
            projected
                .OverseasSites!.Sites[0]
                .BesEvidence!.BesEvidenceUploads.Single(f => f.FileId == "bes-1")
                .BesEvidenceExpiryDate
        );
        AssertSectionsMatch((await LegacyAsync(application))!, projected);
    }

    [Fact]
    public async Task DeleteBesEvidenceFile_ModelNoLongerHoldsTheFile()
    {
        var application = await WithBesEvidenceFileAsync();

        var response = await _client.DeleteAsync(
            AppUrl(application, "overseas-sites/1/bes-evidence/files/bes-1"),
            Ct
        );

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        var projected = await ProjectedAsync(application);
        Assert.Equal(
            "bes-2",
            Assert.Single(projected.OverseasSites!.Sites[0].BesEvidence!.BesEvidenceUploads).FileId
        );
        AssertSectionsMatch((await LegacyAsync(application))!, projected);
    }

    // --- gap 7: rollback on the section and BES routes ------------------------------------------------------------

    // Each route reaches its legacy write against an application with two BES files (a 4xx would fail here, not
    // pass), and a model failure rolls it back.
    [Theory]
    [InlineData("patch-prns")]
    [InlineData("patch-tonnage")]
    [InlineData("patch-sampling-plan")]
    [InlineData("bes-add-file")]
    [InlineData("bes-patch")]
    [InlineData("bes-patch-file")]
    [InlineData("bes-section")]
    public async Task SectionRoute_ModelWriteFails_RollsBackTheLegacyWrite(string route)
    {
        FailModelWrites(services =>
            services.Replace(ServiceDescriptor.Singleton(ScannedUpload("bes-3", "extra.pdf")))
        );
        var application = await WithBesEvidenceFileAsync();

        var response = await SectionWriteAsync(application, route);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal(application.Version, legacy.Version);
        Assert.Equal(application.UpdatedAt, legacy.UpdatedAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(Json(application.Prns), Json(legacy.Prns));
        Assert.Equal(Json(application.SamplingPlan), Json(legacy.SamplingPlan));
        Assert.Equal(Json(application.BesEvidence), Json(legacy.BesEvidence));
        Assert.Equal(
            Json(application.OverseasSites!.Sites[0].BesEvidence),
            Json(legacy.OverseasSites!.Sites[0].BesEvidence)
        );
        Assert.Null(await CurrentApplicationAsync(application));
    }

    private Task<HttpResponseMessage> SectionWriteAsync(
        AccreditationApplicationModel application,
        string route
    ) =>
        route switch
        {
            "patch-prns" => _client.PatchAsJsonAsync(
                AppUrl(application, "prns"),
                new { plannedTonnageBand = "UpTo500" },
                Ct
            ),
            "patch-tonnage" => _client.PatchAsJsonAsync(
                AppUrl(application, "tonnage"),
                new { plannedTonnageBand = "UpTo500" },
                Ct
            ),
            "patch-sampling-plan" => _client.PatchAsJsonAsync(
                AppUrl(application, "sampling-plan"),
                new { files = Array.Empty<object>() },
                Ct
            ),
            "bes-add-file" => _client.PostAsJsonAsync(
                AppUrl(application, "overseas-sites/1/bes-evidence/files"),
                new { fileUploadId = "upload-1" },
                Ct
            ),
            "bes-patch" => _client.PatchAsJsonAsync(
                AppUrl(application, "overseas-sites/1/bes-evidence"),
                new { doYouWantToUploadMoreEvidence = true },
                Ct
            ),
            "bes-patch-file" => _client.PatchAsJsonAsync(
                AppUrl(application, "overseas-sites/1/bes-evidence/files/bes-1"),
                new { besEvidenceExpiryDate = "2028-01-31" },
                Ct
            ),
            "bes-section" => _client.PatchAsJsonAsync(
                AppUrl(application, "bes-evidence"),
                new { sectionStatus = "InProgress" },
                Ct
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };

    // --- gap 8: an accredited site edited in place ----------------------------------------------------------------

    // An application approved with site 001 selected, so the ORS is Active, and a newer one holding the same site.
    private async Task<(
        AccreditationApplicationModel Accredited,
        AccreditationApplicationModel Editing
    )> AccreditedSiteAndANewApplicationAsync(
        Action<OverseasSiteModel>? configure = null,
        Action<OverseasSiteModel>? configureAccredited = null
    )
    {
        var accreditedSite = ReExSite();
        configureAccredited?.Invoke(accreditedSite);
        var accredited = await ApplicationAsync(Guid.NewGuid(), accreditedSite);
        accredited.ApplicationStatus = ApplicationStatus.Approved;
        accredited.Year = 2026;
        accredited.RegistrationReference = "R26ER5000270001ST";
        accredited.AccreditationReference = "A26ER5000270001ST";
        accredited = (await Applications.UpdateAsync(accredited))!;

        var site = ReExSite();
        configure?.Invoke(site);
        var editing = await ApplicationAsync(null, site);
        editing.Year = 2027;
        editing.RegistrationReference = "R26ER5000270001ST";
        return (accredited, (await Applications.UpdateAsync(editing))!);
    }

    [Fact]
    public async Task UpdateOverseasSite_AcceptedActiveSite_GoesToProposedChanges()
    {
        var (accredited, editing) = await AccreditedSiteAndANewApplicationAsync();

        var response = await _client.PatchAsJsonAsync(
            Url(editing, "/1"),
            EditRequest("Renamed"),
            Ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ors = Assert.Single(await CurrentSitesAsync());
        Assert.Equal(OverseasSiteStatus.Active, ors.Status);
        Assert.Equal("ReEx Site", ors.SiteName);
        var entry = Assert.Single(await ApplicationSitesAsync(editing));
        Assert.Equal(ors.EntityId, entry.OverseasSiteId);
        Assert.Equal("Renamed", entry.ProposedChanges!["siteName"].AsString);
        Assert.Equal("Renamed", (await ProjectedAsync(editing)).OverseasSites!.Sites[0].SiteName);
        Assert.Equal("Renamed", (await LegacyAsync(editing))!.OverseasSites!.Sites[0].SiteName);
        // The accredited application and the ORS are untouched by the edit.
        Assert.Equal(
            "ReEx Site",
            (await ProjectedAsync(accredited)).OverseasSites!.Sites[0].SiteName
        );
    }

    [Fact]
    public async Task UpdateInterimSite_ActiveSite_GoesToProposedChanges()
    {
        // The interim site is held by the approved application, so it is Active on the ORS.
        var (accredited, editing) = await AccreditedSiteAndANewApplicationAsync(
            AddStoredInterim,
            AddStoredInterim
        );

        var response = await _client.PatchAsJsonAsync(
            InterimUrl(editing, "/1"),
            InterimRequest("Dock"),
            Ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ors = Assert.Single(await CurrentSitesAsync());
        var interim = Assert.Single(ors.InterimSites);
        Assert.Equal(InterimSiteStatus.Active, interim.Status);
        Assert.Equal("Quay", interim.SiteName);
        var entry = Assert.Single(Assert.Single(await ApplicationSitesAsync(editing)).InterimSites);
        Assert.Equal(interim.SiteNumber, entry.SiteNumber);
        Assert.Equal("Dock", entry.ProposedChanges!["siteName"].AsString);
        Assert.Equal(
            "Dock",
            (await ProjectedAsync(editing)).OverseasSites!.Sites[0].InterimSites[0].SiteName
        );
        Assert.Equal(
            "Quay",
            (await ProjectedAsync(accredited)).OverseasSites!.Sites[0].InterimSites[0].SiteName
        );
    }

    private static void AddStoredInterim(OverseasSiteModel site)
    {
        site.InterimSites = [StoredInterim(1, "001")];
        InterimSiteSync.SyncMirror(site);
    }

    // --- gap 9: a registration group over the transaction limit ---------------------------------------------------

    [Fact]
    public async Task Write_RegistrationGroupOverTheLimit_AnswersFiveHundredAndChangesNothing()
    {
        var application = await ApplicationAsync(null, ReExSite());
        for (var i = 0; i < MaxGroupSize; i++)
        {
            var other = await ApplicationAsync(null, ReExSite());
            other.ApplicationStatus = ApplicationStatus.Withdrawn;
            await Applications.UpdateAsync(other);
        }

        var response = await _client.PatchAsJsonAsync(
            AppUrl(application, "business-plan"),
            new { newInfrastructurePercent = 10, newInfrastructureDetail = "A new baler" },
            Ct
        );

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Null(legacy.BusinessPlan.NewInfrastructurePercent);
        Assert.Null(legacy.RegistrationEntityId);
        Assert.Equal(application.Version, legacy.Version);
        Assert.Empty(
            await Stores.Applications.Versions.FindAllCurrentAsync(
                FilterDefinition<ApplicationEntity>.Empty,
                cancellationToken: Ct
            )
        );
        Assert.Empty(
            await Stores.Registrations.Versions.FindAllCurrentAsync(
                FilterDefinition<RegistrationEntity>.Empty,
                cancellationToken: Ct
            )
        );
        Assert.Empty(await CurrentSitesAsync());
        using var session = await StartSessionAsync();
        Assert.Null(
            await Stores.Organisations.FindByReExOrganisationIdAsync(OrganisationId, session, Ct)
        );
    }

    private const int MaxGroupSize = EprRegisterEnrolBackend
        .DomainModel
        .Migration
        .ApplicationGroupMigrator
        .MaxGroupSize;

    // --- gaps 10 and 11: the seed over HTTP, and the first touch of what it stamped -------------------------------

    private static ReExAccreditationDto SeededReEx() =>
        new()
        {
            AccreditationId = "reex-accreditation",
            OrganisationId = OrganisationId,
            MaterialType = MaterialType.Steel,
            Year = 2026,
            OrganisationName = "Acme Recycling Ltd",
            OrganisationNumber = 500027,
            RegistrationReference = "R26ER5000270001ST",
            SiteAddress = "2 Mill Rd, Leeds",
            IsExporter = true,
            Nation = Nation.England,
            PermitNumbers = ["EPR/AB1234"],
            OverseasSites = [ReExSite()],
        };

    private Task<HttpResponseMessage> SeedAsync() =>
        _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/{OrganisationId}/{ReExRegistrationId}/Steel/seed",
            new { year = 2027 },
            Ct
        );

    private void ReExHoldsTheRegistration() =>
        _reEx
            .GetAccreditationAsync(OrganisationId, ReExRegistrationId, MaterialType.Steel, 2026)
            .Returns(ReExResult<ReExAccreditationDto>.Success(SeededReEx(), 200));

    // The application a seed created, read back through its Location: the JSON body does not carry a usable id.
    private async Task<AccreditationApplicationModel> SeededApplicationAsync(
        HttpResponseMessage response
    )
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = response.Headers.Location!.OriginalString.Split('/')[^1];
        return (await Applications.GetByIdAsync(OrganisationId, id))!;
    }

    private SeedModelStores SeedStores => _factory.Services.GetRequiredService<SeedModelStores>();

    private async Task<RegistrationEntity?> CurrentRegistrationAsync() =>
        await SeedStores.Registrations.Versions.FindCurrentAsync(
            Builders<RegistrationEntity>.Filter.Eq(r => r.ReExRegistrationId, ReExRegistrationId),
            cancellationToken: Ct
        );

    private async Task<List<OverseasSiteEntity>> SeededSitesAsync() =>
        await SeedStores.OverseasSites.Versions.FindAllCurrentAsync(
            FilterDefinition<OverseasSiteEntity>.Empty,
            cancellationToken: Ct
        );

    private async Task<long> CountAsync(string collection) =>
        await _factory
            .Services.GetRequiredService<IMongoDbClientFactory>()
            .GetCollection<BsonDocument>(collection)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

    [Fact]
    public async Task Seed_DualWriteSeedOn_WritesTheEntitiesAndTheInitialApplicationVersionOverHttp()
    {
        Restart(s_seedOnly);
        ReExHoldsTheRegistration();

        var created = await SeededApplicationAsync(await SeedAsync());

        var registration = (await CurrentRegistrationAsync())!;
        Assert.Equal("R26ER5000270001ST", registration.RegistrationNumber);
        Assert.Equal(Nation.England, registration.Nation);
        Assert.Equal("2 Mill Rd, Leeds", registration.Site!.FormattedAddress);
        Assert.Equal(["EPR/AB1234"], registration.PermitNumbers);
        Assert.Equal(registration.EntityId, created.RegistrationEntityId);
        var ors = Assert.Single(await SeededSitesAsync());
        Assert.Equal("001", ors.OrsId);
        Assert.Equal("ReEx Site", ors.SiteName);
        Assert.Equal(registration.EntityId, ors.RegistrationId);
        Assert.Equal(ors.EntityId, Assert.Single(created.OverseasSites!.Sites).OverseasSiteEntityId);
        using var session = await StartSessionAsync();
        var organisation = (
            await SeedStores.Organisations.FindByReExOrganisationIdAsync(
                OrganisationId,
                session,
                Ct
            )
        )!;
        Assert.Equal("Acme Recycling Ltd", organisation.Name);
        Assert.Equal(500027, organisation.OrganisationNumber);
        // A seed is when a user starts an application, so it writes the initial Application version too, even with
        // DualWrite off (the team only ever enables seed dual-write and full dual-write together).
        var entity = (await CurrentApplicationAsync(created))!;
        Assert.Equal(1, entity.Version);
        Assert.Equal(registration.EntityId, entity.RegistrationId);
        Assert.Equal(
            ors.EntityId,
            Assert.Single(entity.AsAccreditation().ApplicationOverseasSites).OverseasSiteId
        );
    }

    [Fact]
    public async Task Seed_DualWriteSeedOff_WritesNoModel()
    {
        Restart(new() { ["DomainModel:ExpandEnabled"] = "true" });
        ReExHoldsTheRegistration();

        var created = await SeededApplicationAsync(await SeedAsync());

        Assert.Null(created.RegistrationEntityId);
        Assert.Equal(0, await CountAsync(DomainModelCollections.Organisations));
        Assert.Equal(0, await CountAsync(DomainModelCollections.RegistrationVersions));
        Assert.Equal(0, await CountAsync(DomainModelCollections.OverseasSiteVersions));
        Assert.Equal(0, await CountAsync(DomainModelCollections.ApplicationVersions));
    }

    [Fact]
    public async Task FirstWrite_AfterTheSeed_VersionsTheSeedsApplicationAndReusesItsRegistrationAndSites()
    {
        Restart(s_seedAndDualWriteOn);
        ReExHoldsTheRegistration();
        var created = await SeededApplicationAsync(await SeedAsync());
        var seededRegistration = (await CurrentRegistrationAsync())!;
        var seededSite = Assert.Single(await SeededSitesAsync());
        var seeded = (await CurrentApplicationAsync(created))!;
        Assert.Equal(1, seeded.Version);

        var response = await _client.PatchAsJsonAsync(
            AppUrl(created, "business-plan"),
            new { newInfrastructurePercent = 10, newInfrastructureDetail = "A new baler" },
            Ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entity = (await CurrentApplicationAsync(created))!;
        Assert.Equal(seeded.EntityId, entity.EntityId);
        Assert.Equal(2, entity.Version);
        Assert.Equal(seededRegistration.EntityId, entity.RegistrationId);
        var registration = (await CurrentRegistrationAsync())!;
        Assert.Equal(seededRegistration.EntityId, registration.EntityId);
        Assert.Equal(seededRegistration.Version, registration.Version);
        var site = Assert.Single(await SeededSitesAsync());
        Assert.Equal(seededSite.EntityId, site.EntityId);
        Assert.Equal(seededSite.Version, site.Version);
        Assert.Equal(1, await CountAsync(DomainModelCollections.Organisations));
        Assert.Equal(
            seededSite.EntityId,
            Assert.Single(entity.AsAccreditation().ApplicationOverseasSites).OverseasSiteId
        );
        var projected = await ProjectedAsync(created);
        Assert.Equal(10, projected.BusinessPlan.NewInfrastructurePercent);
        Assert.Equal("R26ER5000270001ST", projected.RegistrationReference);
        Assert.Equal(500027, projected.OrgId);
    }
}
