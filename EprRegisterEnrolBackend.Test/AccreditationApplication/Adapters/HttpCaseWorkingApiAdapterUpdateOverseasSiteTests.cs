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
/// RA-682, RA-767: a change request holding adds and ORS updates goes to ManagementBe as one submission, under the
/// request's id: add-ORS when it holds any add, each site naming its own type, adds first, with the charge and payment
/// reference only when it holds an add.
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
    public async Task A_mixed_request_is_sent_as_one_add_submission_with_each_sites_own_type()
    {
        var handler = new QueuedHandler(
            (
                HttpStatusCode.Created,
                Response(
                    "SUB-2026-0001",
                    (902504, "IYC-ORS-2026-00001"),
                    (902501, "IYC-ORS-2026-00002"),
                    (902502, "IYC-ORS-2026-00003")
                )
            )
        );
        var request = Request(
            Update(902501, "001", "Gdansk Recycling Renamed"),
            new ChangeRequestSite { SiteId = 902504, OrsId = "004", SiteName = "Ghent Recycling NV", ChargePence = 32800 },
            Update(902502, "002", "Porto Reciclagem Renamed")
        );

        var result = await AdapterFor(handler).SubmitChangeRequestAsync(request, Accreditation, Ct);

        var body = handler.Bodies.Should().ContainSingle().Subject;
        body["typeId"]!.GetValue<string>().Should().Be("add-overseas-site");
        var payload = body["payload"]!.AsObject();
        payload["operatorRequestId"]!.GetValue<string>().Should().Be(request.RequestId);
        payload["paymentReference"]!.GetValue<string>().Should().Be("PR/PK/EX/500250");
        payload["chargePence"]!.GetValue<int>().Should().Be(32800);
        // ManagementBe sets the submission's reference and size.
        payload.ContainsKey("submissionReference").Should().BeFalse();
        payload.ContainsKey("submissionSize").Should().BeFalse();
        payload.ContainsKey("changeType").Should().BeFalse();
        // Adds first, then the request's order.
        payload["sites"]!
            .AsArray()
            .Select(s => (s!["orsId"]!.GetValue<string>(), s["typeId"]!.GetValue<string>()))
            .Should()
            .Equal(
                ("004", "add-overseas-site"),
                ("001", "update-overseas-site"),
                ("002", "update-overseas-site")
            );
        var update = payload["sites"]![1]!;
        update["siteName"]!.GetValue<string>().Should().Be("Gdansk Recykling Sp. z o.o.");
        update["previous"]!["siteName"]!.GetValue<string>().Should().Be("Gdansk Recykling Sp. z o.o.");
        update["proposed"]!["siteName"]!.GetValue<string>().Should().Be("Gdansk Recycling Renamed");
        update["country"]!.GetValue<string>().Should().Be("Poland");

        result.SubmissionReferences.Should().Equal("SUB-2026-0001");
        result.Sites.Select(s => (s.SiteId, s.CaseReference))
            .Should()
            .Equal((902501, "IYC-ORS-2026-00002"), (902504, "IYC-ORS-2026-00001"), (902502, "IYC-ORS-2026-00003"));
    }

    [Fact]
    public async Task An_update_only_request_is_one_free_update_submission_under_the_request_id()
    {
        var handler = new QueuedHandler((HttpStatusCode.Created, Response("SUB-2026-0003", (902503, "IYC-ORS-2026-00004"))));
        var request = Request(Update(902503, "003", "Milano Riciclo Renamed"));

        var result = await AdapterFor(handler).SubmitChangeRequestAsync(request, Accreditation, Ct);

        var body = handler.Bodies.Should().ContainSingle().Subject;
        body["typeId"]!.GetValue<string>().Should().Be("update-overseas-site");
        body["payload"]!["operatorRequestId"]!.GetValue<string>().Should().Be(request.RequestId);
        body["payload"]!.AsObject().ContainsKey("paymentReference").Should().BeFalse();
        body["payload"]!["chargePence"]!.GetValue<int>().Should().Be(0);
        body["payload"]!["sites"]![0]!["typeId"]!.GetValue<string>().Should().Be("update-overseas-site");
        result.SubmissionReference.Should().Be("SUB-2026-0003");
        result.Sites.Single().CaseReference.Should().Be("IYC-ORS-2026-00004");
    }

    // RA-767 AC2: a mixed request is one POST, so a refusal is the whole request's, with its own status.
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_mixed_request_refused_is_one_failure_with_its_status(HttpStatusCode status)
    {
        var handler = new QueuedHandler((status, new { title = "Refused" }));
        var request = Request(
            new ChangeRequestSite { SiteId = 902504, OrsId = "004", SiteName = "Ghent Recycling NV", ChargePence = 32800 },
            Update(902501, "001", "Gdansk Recycling Renamed")
        );

        var act = () => AdapterFor(handler).SubmitChangeRequestAsync(request, Accreditation, Ct);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(status);
        handler.Bodies.Should().ContainSingle();
    }

    // A defect in any site is raised before anything is sent.
    [Fact]
    public async Task A_mixed_request_with_a_defect_sends_nothing()
    {
        var handler = new QueuedHandler();
        var update = Update(902501, "001", "Gdansk Recycling Renamed");
        update.SiteId = 999999;
        var request = Request(
            new ChangeRequestSite { SiteId = 902504, OrsId = "004", SiteName = "Ghent Recycling NV", ChargePence = 32800 },
            update
        );

        var act = () => AdapterFor(handler).SubmitChangeRequestAsync(request, Accreditation, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Bodies.Should().BeEmpty();
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
