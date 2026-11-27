using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.DevSeed;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

/// <summary>
/// RA-682: a change request holding adds and ORS updates goes to ManagementBe as one submission per change type,
/// adds first: each under its own operatorRequestId (adds keep the request's id, updates add
/// <c>:update-overseas-site</c>), with the charge and payment reference only on the add submission.
/// </summary>
public sealed class HttpCaseWorkingApiAdapterUpdateOverseasSiteTests
{
    private const string Url = "http://management-be";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AccreditationApplicationModel Accreditation =
        DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearUpdate, 2026);

    private static ChangeRequestSite Update(int siteId, string orsId, string proposedName)
    {
        var ors = ChangeRequestRules.FindSite(Accreditation, siteId)!;
        var previous = OverseasSiteDetails.From(ors);
        return new ChangeRequestSite
        {
            ChangeType = AccreditationChangeRequest.UpdateOverseasSiteChangeType,
            SiteId = siteId,
            OrsId = orsId,
            SiteName = ors.SiteName,
            Previous = previous,
            Proposed = previous with { SiteName = proposedName },
            ChargePence = 0,
        };
    }

    private static AccreditationChangeRequest Request(params ChangeRequestSite[] sites) =>
        new()
        {
            OrganisationId = Accreditation.OrganisationId,
            ApplicationId = Accreditation.ApplicationId!,
            RegistrationId = Accreditation.RegistrationId,
            MaterialType = Accreditation.MaterialType,
            Year = 2026,
            AccreditationReference = Accreditation.AccreditationReference,
            ChargePence = sites.Count(s => !s.IsUpdate) * 32800,
            PaymentReference = sites.Any(s => !s.IsUpdate) ? "PR/PK/EX/500250" : null,
            SubmittedBy = new SubmittedByModel { FullName = "Jane Smith", JobTitle = "Director", Email = "jane@example.com" },
            Sites = [.. sites],
        };

    private static object Response(string submissionReference, params (int SiteId, string Reference)[] items) =>
        new
        {
            submissionReference,
            workItems = items.Select(i => new { siteId = i.SiteId, id = Guid.NewGuid(), applicationReference = i.Reference }),
        };

    [Fact]
    public async Task A_mixed_request_is_sent_as_an_add_submission_then_an_update_submission()
    {
        var handler = new QueuedHandler(
            (HttpStatusCode.Created, Response("SUB-2026-0001", (902504, "IYC-ORS-2026-00001"))),
            (HttpStatusCode.Created, Response("SUB-2026-0002", (902501, "IYC-ORS-2026-00002"), (902502, "IYC-ORS-2026-00003")))
        );
        var request = Request(
            Update(902501, "001", "Gdansk Recycling Renamed"),
            new ChangeRequestSite { SiteId = 902504, OrsId = "004", SiteName = "Ghent Recycling NV", ChargePence = 32800 },
            Update(902502, "002", "Porto Reciclagem Renamed")
        );

        var result = await AdapterFor(handler).SubmitChangeRequestAsync(request, Accreditation, Ct);

        handler.Bodies.Should().HaveCount(2);
        var add = handler.Bodies[0];
        add["typeId"]!.GetValue<string>().Should().Be("add-overseas-site");
        add["payload"]!["operatorRequestId"]!.GetValue<string>().Should().Be(request.RequestId);
        add["payload"]!["paymentReference"]!.GetValue<string>().Should().Be("PR/PK/EX/500250");
        add["payload"]!["chargePence"]!.GetValue<int>().Should().Be(32800);
        add["payload"]!["sites"]!.AsArray().Select(s => s!["orsId"]!.GetValue<string>()).Should().Equal("004");

        var update = handler.Bodies[1];
        update["typeId"]!.GetValue<string>().Should().Be("update-overseas-site");
        update["payload"]!["operatorRequestId"]!.GetValue<string>().Should().Be($"{request.RequestId}:update-overseas-site");
        update["payload"]!["changeType"]!.GetValue<string>().Should().Be("update-overseas-site");
        // RA-767: one request is one submission: the update group reuses the add group's reference, and both carry
        // the whole request's size.
        add["payload"]!.AsObject().ContainsKey("submissionReference").Should().BeFalse();
        update["payload"]!["submissionReference"]!.GetValue<string>().Should().Be("SUB-2026-0001");
        add["payload"]!["submissionSize"]!.GetValue<int>().Should().Be(3);
        update["payload"]!["submissionSize"]!.GetValue<int>().Should().Be(3);
        update["payload"]!.AsObject().ContainsKey("paymentReference").Should().BeFalse();
        update["payload"]!["chargePence"]!.GetValue<int>().Should().Be(0);
        var site = update["payload"]!["sites"]![0]!;
        site["orsId"]!.GetValue<string>().Should().Be("001");
        site["siteName"]!.GetValue<string>().Should().Be("Gdansk Recykling Sp. z o.o.");
        site["previous"]!["siteName"]!.GetValue<string>().Should().Be("Gdansk Recykling Sp. z o.o.");
        site["proposed"]!["siteName"]!.GetValue<string>().Should().Be("Gdansk Recycling Renamed");
        site["country"]!.GetValue<string>().Should().Be("Poland");

        result.SubmissionReference.Should().Be("SUB-2026-0001");
        result.Sites.Select(s => (s.SiteId, s.CaseReference))
            .Should()
            .Equal((902504, "IYC-ORS-2026-00001"), (902501, "IYC-ORS-2026-00002"), (902502, "IYC-ORS-2026-00003"));
    }

    [Fact]
    public async Task An_update_only_request_is_one_update_submission_under_its_suffixed_id()
    {
        var handler = new QueuedHandler((HttpStatusCode.Created, Response("SUB-2026-0003", (902503, "IYC-ORS-2026-00004"))));
        var request = Request(Update(902503, "003", "Milano Riciclo Renamed"));

        var result = await AdapterFor(handler).SubmitChangeRequestAsync(request, Accreditation, Ct);

        var body = handler.Bodies.Should().ContainSingle().Subject;
        body["typeId"]!.GetValue<string>().Should().Be("update-overseas-site");
        body["payload"]!["operatorRequestId"]!.GetValue<string>().Should().Be($"{request.RequestId}:update-overseas-site");
        result.SubmissionReference.Should().Be("SUB-2026-0003");
        result.Sites.Single().CaseReference.Should().Be("IYC-ORS-2026-00004");
    }

    // ManagementBe holds the add submission but refused the update one before saving it (a 400): the add sites are
    // held and the update sites certainly aren't.
    [Fact]
    public async Task An_update_submission_refused_after_the_add_was_accepted_reports_which_groups_are_held()
    {
        var handler = new QueuedHandler(
            (HttpStatusCode.Created, Response("SUB-2026-0004", (902504, "IYC-ORS-2026-00005"))),
            (HttpStatusCode.BadRequest, new { title = "Unknown work item type" })
        );
        var request = Request(
            new ChangeRequestSite { SiteId = 902504, OrsId = "004", SiteName = "Ghent Recycling NV", ChargePence = 32800 },
            Update(902501, "001", "Gdansk Recycling Renamed")
        );

        var act = () => AdapterFor(handler).SubmitChangeRequestAsync(request, Accreditation, Ct);

        var thrown = (await act.Should().ThrowAsync<ChangeRequestPartlySubmittedException>()).Which;
        thrown.HeldChangeTypes.Should().Equal("add-overseas-site");
        thrown.RefusedChangeType.Should().Be("update-overseas-site");
        thrown.RefusedMayBeHeld.Should().BeFalse();
        thrown.InnerException.Should().BeOfType<HttpRequestException>()
            .Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_update_submission_that_fails_with_a_5xx_may_be_held()
    {
        var handler = new QueuedHandler(
            (HttpStatusCode.Created, Response("SUB-2026-0006", (902504, "IYC-ORS-2026-00006"))),
            (HttpStatusCode.BadGateway, new { title = "Bad gateway" })
        );
        var request = Request(
            new ChangeRequestSite { SiteId = 902504, OrsId = "004", SiteName = "Ghent Recycling NV", ChargePence = 32800 },
            Update(902501, "001", "Gdansk Recycling Renamed")
        );

        var act = () => AdapterFor(handler).SubmitChangeRequestAsync(request, Accreditation, Ct);

        (await act.Should().ThrowAsync<ChangeRequestPartlySubmittedException>()).Which.RefusedMayBeHeld.Should().BeTrue();
    }

    // RA-767: ManagementBe answers the later group with the reference it was sent, which is held once.
    [Fact]
    public async Task A_reference_shared_by_every_group_is_returned_once()
    {
        var handler = new QueuedHandler(
            (HttpStatusCode.Created, Response("SUB-2026-0010", (902504, "IYC-ORS-2026-00010"))),
            (HttpStatusCode.Created, Response("SUB-2026-0010", (902501, "IYC-ORS-2026-00011")))
        );
        var request = Request(
            new ChangeRequestSite { SiteId = 902504, OrsId = "004", SiteName = "Ghent Recycling NV", ChargePence = 32800 },
            Update(902501, "001", "Gdansk Recycling Renamed")
        );

        var result = await AdapterFor(handler).SubmitChangeRequestAsync(request, Accreditation, Ct);

        result.SubmissionReferences.Should().Equal("SUB-2026-0010");
    }

    [Fact]
    public async Task Every_submission_reference_is_returned()
    {
        var handler = new QueuedHandler(
            (HttpStatusCode.Created, Response("SUB-2026-0007", (902504, "IYC-ORS-2026-00007"))),
            (HttpStatusCode.Created, Response("SUB-2026-0008", (902501, "IYC-ORS-2026-00008")))
        );
        var request = Request(
            new ChangeRequestSite { SiteId = 902504, OrsId = "004", SiteName = "Ghent Recycling NV", ChargePence = 32800 },
            Update(902501, "001", "Gdansk Recycling Renamed")
        );

        var result = await AdapterFor(handler).SubmitChangeRequestAsync(request, Accreditation, Ct);

        result.SubmissionReferences.Should().Equal("SUB-2026-0007", "SUB-2026-0008");
    }

    // A defect in a later group is raised before that group is sent, so ManagementBe certainly holds none of it.
    [Fact]
    public async Task A_defect_in_a_later_group_is_reported_as_certainly_not_held()
    {
        var handler = new QueuedHandler((HttpStatusCode.Created, Response("SUB-2026-0009", (902504, "IYC-ORS-2026-00009"))));
        var update = Update(902501, "001", "Gdansk Recycling Renamed");
        update.SiteId = 999999;
        var request = Request(
            new ChangeRequestSite { SiteId = 902504, OrsId = "004", SiteName = "Ghent Recycling NV", ChargePence = 32800 },
            update
        );

        var act = () => AdapterFor(handler).SubmitChangeRequestAsync(request, Accreditation, Ct);

        var thrown = (await act.Should().ThrowAsync<ChangeRequestPartlySubmittedException>()).Which;
        thrown.RefusedMayBeHeld.Should().BeFalse();
        thrown.InnerException.Should().BeOfType<InvalidOperationException>();
        handler.Bodies.Should().ContainSingle();
    }

    // AC02: an update can add BES evidence: it goes as addedBesEvidence, beside the ORS's own unchanged besEvidence.
    [Fact]
    public async Task An_updates_added_BES_evidence_is_sent_apart_from_the_ORS_own()
    {
        var handler = new QueuedHandler((HttpStatusCode.Created, Response("SUB-2026-0010", (902503, "IYC-ORS-2026-00010"))));
        var update = Update(902503, "003", "Milano Riciclo Renamed");
        update.BesEvidence = new BesEvidenceModel
        {
            BesEvidenceUploads =
            [
                new BesEvidenceFileModel { FileId = "new-1", Filename = "new.pdf", S3Key = "bes-evidence/new-1", ScanStatus = "Clean" },
            ],
        };

        await AdapterFor(handler).SubmitChangeRequestAsync(Request(update), Accreditation, Ct);

        var site = handler.Bodies.Single()["payload"]!["sites"]![0]!;
        site["addedBesEvidence"]!["files"]!.AsArray().Select(f => f!["fileId"]!.GetValue<string>()).Should().Equal("new-1");
        var held = ChangeRequestRules.FindSite(Accreditation, 902503)!.BesEvidence?.BesEvidenceUploads ?? [];
        site["besEvidence"]!["files"]!.AsArray().Should().HaveCount(held.Count);
    }

    [Fact]
    public async Task An_update_without_added_BES_evidence_sends_none()
    {
        var handler = new QueuedHandler((HttpStatusCode.Created, Response("SUB-2026-0011", (902503, "IYC-ORS-2026-00011"))));

        await AdapterFor(handler).SubmitChangeRequestAsync(Request(Update(902503, "003", "Renamed")), Accreditation, Ct);

        handler.Bodies.Single()["payload"]!["sites"]![0]!.AsObject().ContainsKey("addedBesEvidence").Should().BeFalse();
    }

    [Fact]
    public async Task An_update_whose_ORS_is_not_found_is_not_sent()
    {
        var handler = new QueuedHandler();
        var update = Update(902501, "001", "Gdansk Recycling Renamed");
        update.SiteId = 999999;
        var request = Request(update);

        var act = () => AdapterFor(handler).SubmitChangeRequestAsync(request, Accreditation, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Bodies.Should().BeEmpty();
    }

    [Fact]
    public async Task An_add_submission_refused_first_is_reported_with_its_own_status()
    {
        var handler = new QueuedHandler((HttpStatusCode.BadRequest, new { title = "Bad" }));
        var request = Request(
            new ChangeRequestSite { SiteId = 902504, OrsId = "004", SiteName = "Ghent Recycling NV", ChargePence = 32800 },
            Update(902501, "001", "Gdansk Recycling Renamed")
        );

        var act = () => AdapterFor(handler).SubmitChangeRequestAsync(request, Accreditation, Ct);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        handler.Bodies.Should().ContainSingle();
    }

    private static HttpCaseWorkingApiAdapter AdapterFor(HttpMessageHandler handler)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        return new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            Options.Create(new CaseWorkingApiConfig { Url = Url, ClientId = "operator-backend" }),
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
    }

    // Answers each request with the next queued response, and keeps every request body.
    private sealed class QueuedHandler(params (HttpStatusCode Status, object Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, object Body)> _responses = new(responses);

        public List<JsonNode> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            var (status, body) = _responses.Dequeue();
            return new HttpResponseMessage(status) { Content = JsonContent.Create(body) };
        }
    }
}
