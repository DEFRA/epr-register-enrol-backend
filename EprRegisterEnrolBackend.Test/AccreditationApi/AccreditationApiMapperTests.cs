using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApi;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Status;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationApi;

public class AccreditationApiMapperTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly DateTime ApprovedAt = new(2027, 1, 10, 9, 30, 0, DateTimeKind.Utc);

    private static AccreditationApplicationModel Application(
        bool exporter = false,
        MaterialType material = MaterialType.Plastic,
        Action<AccreditationApplicationModel>? configure = null
    )
    {
        var application = new AccreditationApplicationModel
        {
            OrganisationId = "reex-internal-object-id",
            OrgId = 500027,
            OrganisationName = "Egger (UK) Limited",
            Year = 2027,
            MaterialType = material,
            ApplicationStatus = ApplicationStatus.Approved,
            RegistrationReference = "R27ER5000270036PL",
            AccreditationReference = "A27ER5000270063PL",
            ApplicationReference = "RA-000000042",
            CaseManagementWorkItemId = Guid.NewGuid(),
            IsExporter = exporter,
            WasteProcessingType = exporter ? "exporter" : "reprocessor",
            SiteAddress = "Anick Grange Road, Hexham, NE46 4JS",
            CaseManagementStatusUpdatedAt = ApprovedAt,
        };
        configure?.Invoke(application);
        return application;
    }

    private static JsonElement Accreditation(AccreditationApplicationModel application) =>
        JsonSerializer.SerializeToElement(
            AccreditationApiMapper.ToAccreditation(application),
            Web
        );

    private static JsonElement Summary(AccreditationApplicationModel application) =>
        JsonSerializer.SerializeToElement(AccreditationApiMapper.ToSummary(application), Web);

    [Fact]
    public void Reprocessor_CarriesItsIdentifyingFields()
    {
        var json = Accreditation(Application());

        json.GetProperty("organisationNumber").GetInt32().Should().Be(500027);
        json.GetProperty("accreditationNumber").GetString().Should().Be("A27ER5000270063PL");
        json.GetProperty("registrationNumber").GetString().Should().Be("R27ER5000270036PL");
        json.GetProperty("accreditationYear").GetInt32().Should().Be(2027);
        json.TryGetProperty("status", out _).Should().BeFalse();
        json.GetProperty("latestStatus").GetProperty("status").GetString().Should().Be("approved");
        json.GetProperty("material").GetString().Should().Be("Plastic");
        json.GetProperty("wasteProcessingType").GetString().Should().Be("reprocessor");
    }

    [Fact]
    public void Accreditation_DoesNotCarryApplicationOnlyOrSensitiveData()
    {
        var json = Accreditation(
            Application(
                exporter: true,
                configure: a =>
                {
                    a.BusinessPlan.NewInfrastructurePercent = 60;
                    a.SubmitterContactDetails = new SubmitterContactDetailsModel
                    {
                        FullName = "Iva",
                    };
                    a.OverseasSites = new AccreditationApplicationOverseasSites
                    {
                        Sites =
                        [
                            new OverseasSiteModel
                            {
                                SiteId = 1,
                                OrsId = "001",
                                SiteName = "A",
                            },
                        ],
                    };
                }
            )
        );

        foreach (
            var name in new[]
            {
                "businessPlan",
                "prnIssuance",
                "submitterContactDetails",
                "siteAddress",
                "accreditationReference",
                "year",
            }
        )
        {
            json.TryGetProperty(name, out _).Should().BeFalse(name);
        }

        // RA-647: an exporter carries its accredited overseas sites in the same response.
        json.GetProperty("overseasSites").GetArrayLength().Should().Be(1);
    }

    // RA-647: a malformed record is dropped and logged; the rest of the page is still served.
    [Fact]
    public void ToSummaries_DropsAndLogsAMalformedRecord_AndKeepsTheRest()
    {
        var good = Application();
        var noRegistration = Application(configure: a => a.RegistrationReference = null);
        var noAccreditation = Application(configure: a => a.AccreditationReference = " ");
        var noOrganisation = Application(configure: a => a.OrgId = null);
        foreach (var malformed in new[] { noRegistration, noAccreditation, noOrganisation })
            malformed.Id = ObjectId.GenerateNewId();
        var logger = new CapturingLogger<AccreditationApiMapperTests>();

        var summaries = AccreditationApiMapper.ToSummaries(
            [noRegistration, good, noAccreditation, noOrganisation],
            logger
        );

        summaries.Should().ContainSingle().Which.AccreditationNumber.Should().Be("A27ER5000270063PL");
        logger
            .Entries.Select(e => (e.LogLevel, e.Message))
            .Should()
            .BeEquivalentTo(
                [
                    (LogLevel.Warning, Dropped(noRegistration, "no registration number")),
                    (LogLevel.Warning, Dropped(noAccreditation, "no accreditation number")),
                    (LogLevel.Warning, Dropped(noOrganisation, "no organisation number")),
                ]
            );
    }

    // The fields an operator searches the logs by when a record is dropped (RA-651).
    [Fact]
    public void ToSummaries_LogsADroppedRecordWithItsIdAndReason_AsStructuredFields()
    {
        var malformed = Application(configure: a => a.RegistrationReference = null);
        malformed.Id = ObjectId.GenerateNewId();
        var logs = new CapturingLoggerFactory();

        AccreditationApiMapper.ToSummaries(
            [malformed],
            logs.CreateLogger("EprRegisterEnrolBackend.AccreditationApi")
        );

        var entry = logs.Entries.Should().ContainSingle().Subject;
        entry.LogLevel.Should().Be(LogLevel.Warning);
        entry.Properties["ApplicationId"].Should().Be(malformed.Id);
        entry.Properties["Reason"].Should().Be("no registration number");
    }

    private static string Dropped(AccreditationApplicationModel application, string reason) =>
        $"Dropped a malformed accreditation from the response: applicationId={application.Id} reason={reason}";

    [Fact]
    public void ToSummaries_LogsNothingWhenEveryRecordIsWellFormed()
    {
        var logger = new CapturingLogger<AccreditationApiMapperTests>();

        var summaries = AccreditationApiMapper.ToSummaries([Application()], logger);

        summaries.Should().HaveCount(1);
        logger.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(GlassRecyclingProcess.Remelt, "glass_re_melt")]
    [InlineData(GlassRecyclingProcess.Other, "glass_other")]
    public void Glass_CarriesItsRecyclingProcess(GlassRecyclingProcess process, string expected)
    {
        var json = Accreditation(
            Application(
                material: MaterialType.Glass,
                configure: a => a.GlassRecyclingProcess = process
            )
        );

        json.GetProperty("material").GetString().Should().Be("Glass");
        json.GetProperty("glassRecyclingProcess").GetString().Should().Be(expected);
    }

    [Fact]
    public void NonGlass_HasNoRecyclingProcess_EvenIfOneWasStored()
    {
        var json = Accreditation(
            Application(configure: a => a.GlassRecyclingProcess = GlassRecyclingProcess.Remelt)
        );

        json.TryGetProperty("glassRecyclingProcess", out _).Should().BeFalse();
    }

    [Fact]
    public void Exporter_IsAnExporter()
    {
        Accreditation(Application(exporter: true))
            .GetProperty("wasteProcessingType")
            .GetString()
            .Should()
            .Be("exporter");
    }

    [Theory]
    [InlineData(PlannedTonnageBand.UpTo500, "up_to_500")]
    [InlineData(PlannedTonnageBand.UpTo5000, "up_to_5000")]
    [InlineData(PlannedTonnageBand.UpTo10000, "up_to_10000")]
    [InlineData(PlannedTonnageBand.Over10000, "over_10000")]
    public void TonnageBand_UsesTheReExValues_AndHasOneHistoryEntry(
        PlannedTonnageBand band,
        string expected
    )
    {
        var json = Accreditation(Application(configure: a => a.Prns.PlannedTonnageBand = band));

        json.GetProperty("tonnageBand").GetString().Should().Be(expected);
        var entry = json.GetProperty("tonnageBandHistory").EnumerateArray().Single();
        entry.GetProperty("tonnageBand").GetString().Should().Be(expected);
        entry.GetProperty("recordedAt").GetDateTime().Should().Be(ApprovedAt);
        entry.TryGetProperty("effectiveFromDate", out _).Should().BeFalse();
    }

    [Fact]
    public void NoTonnageBandYet_LeavesItOut_AndHasNoHistory()
    {
        var json = Accreditation(Application());

        json.TryGetProperty("tonnageBand", out _).Should().BeFalse();
        json.GetProperty("tonnageBandHistory").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Signatories_CarryNameAndEmail_AndNothingInternal()
    {
        var json = Accreditation(
            Application(configure: a =>
                a.Prns.Authorisers = [
                    new PrnsAuthoriser
                    {
                        FullName = "Iva Cruickshank",
                        Email = "iva@example.com",
                        IsNew = true,
                    },
                ]
            )
        );

        var signatory = json.GetProperty("signatories").EnumerateArray().Single();
        signatory.GetProperty("fullName").GetString().Should().Be("Iva Cruickshank");
        signatory.GetProperty("email").GetString().Should().Be("iva@example.com");
        signatory.TryGetProperty("isNew", out _).Should().BeFalse();
    }

    [Fact]
    public void Summary_NeverCarriesSignatoriesOrHistory()
    {
        var json = Summary(
            Application(configure: a =>
            {
                a.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo500;
                a.Prns.Authorisers =
                [
                    new PrnsAuthoriser { FullName = "Iva", Email = "iva@example.com" },
                ];
            })
        );

        json.GetProperty("tonnageBand").GetString().Should().Be("up_to_500");
        json.TryGetProperty("signatories", out _).Should().BeFalse();
        json.TryGetProperty("statusHistory", out _).Should().BeFalse();
        json.TryGetProperty("tonnageBandHistory", out _).Should().BeFalse();
    }

    [Fact]
    public void StatusHistory_IsOneApprovedEntry_RecordedWhenTheApplicationWasApproved()
    {
        var json = Accreditation(Application());

        var entry = json.GetProperty("statusHistory").EnumerateArray().Single();
        entry.GetProperty("status").GetString().Should().Be("approved");
        entry.GetProperty("effectiveFromDate").GetString().Should().Be("2027-01-10");
        entry.GetProperty("effectiveToDate").ValueKind.Should().Be(JsonValueKind.Null);
        entry.TryGetProperty("effectiveDate", out _).Should().BeFalse();
        entry.GetProperty("recordedAt").GetDateTime().Should().Be(ApprovedAt);
    }

    [Fact]
    public void StatusHistory_FallsBackToTheLastEdit_WhenNoStatusPushWasRecorded()
    {
        var edited = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);

        var json = Accreditation(
            Application(configure: a =>
            {
                a.CaseManagementStatusUpdatedAt = null;
                a.DateLastEdited = edited;
            })
        );

        json.GetProperty("statusHistory")[0]
            .GetProperty("recordedAt")
            .GetDateTime()
            .Should()
            .Be(edited);
    }

    [Fact]
    public void EffectiveFromDate_IsTheUkDate_NotTheUtcDate()
    {
        // 23:30 UTC on 30 June is 00:30 on 1 July in the UK (BST).
        var json = Accreditation(
            Application(configure: a =>
                a.CaseManagementStatusUpdatedAt = new DateTime(
                    2027,
                    6,
                    30,
                    23,
                    30,
                    0,
                    DateTimeKind.Utc
                )
            )
        );

        json.GetProperty("statusHistory")[0]
            .GetProperty("effectiveFromDate")
            .GetString()
            .Should()
            .Be("2027-07-01");
    }

    [Fact]
    public void AStatusRecordedBeforeTheAccreditationYear_AppliesFromThe1stOfJanuary()
    {
        var json = Accreditation(
            Application(configure: a => a.CaseManagementStatusUpdatedAt = Nov2026)
        );

        json.GetProperty("statusHistory")[0]
            .GetProperty("effectiveFromDate")
            .GetString()
            .Should()
            .Be("2027-01-01");
    }

    // ---- building a history from several changes ----

    private static readonly DateTime Nov2026 = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void History_EachStatusApplies_UntilTheDayBeforeTheNextOne_AndTheLatestHasNoEnd()
    {
        var history = StatusHistoryBuilder.Build(
            [
                ("approved", Nov2026),
                ("suspended", new DateTime(2027, 3, 10, 12, 0, 0, DateTimeKind.Utc)),
                ("approved", new DateTime(2027, 5, 1, 12, 0, 0, DateTimeKind.Utc)),
            ],
            2027
        );

        history
            .Select(e => (e.Status, e.EffectiveFromDate, e.EffectiveToDate))
            .Should()
            .Equal(
                ("approved", new DateOnly(2027, 1, 1), (DateOnly?)new DateOnly(2027, 3, 9)),
                ("suspended", new DateOnly(2027, 3, 10), (DateOnly?)new DateOnly(2027, 4, 30)),
                ("approved", new DateOnly(2027, 5, 1), (DateOnly?)null)
            );
    }

    [Fact]
    public void History_IsOrderedByWhenItAppliesFrom_NotByTheOrderGiven()
    {
        var history = StatusHistoryBuilder.Build(
            [
                ("suspended", new DateTime(2027, 3, 10, 12, 0, 0, DateTimeKind.Utc)),
                ("approved", Nov2026),
            ],
            2027
        );

        history.Select(e => e.Status).Should().Equal("approved", "suspended");
    }

    // Both are recorded before 2027, so both apply from 1 January 2027. The one recorded later takes
    // precedence: it comes last, and is the status that applies. The one it supersedes is kept, and ends
    // on that same date, so no period ends before it starts.
    [Fact]
    public void History_WhereTwoStatusesApplyFromTheSameDate_TheLaterRecordedComesLast()
    {
        var history = StatusHistoryBuilder.Build(
            [("suspended", Nov2026.AddDays(5)), ("approved", Nov2026)],
            2027
        );

        history.Select(e => e.Status).Should().Equal("approved", "suspended");
        history
            .Select(e => e.EffectiveFromDate)
            .Should()
            .AllBeEquivalentTo(new DateOnly(2027, 1, 1));
        history[0].EffectiveToDate.Should().Be(new DateOnly(2027, 1, 1));
        history[^1].EffectiveToDate.Should().BeNull();
        history
            .Where(e => e.EffectiveToDate is not null)
            .Should()
            .OnlyContain(e => e.EffectiveToDate >= e.EffectiveFromDate);
    }

    [Fact]
    public void History_ASupersededStatusInTheMiddle_EndsOnItsOwnStartDate_AndTheRestFollow()
    {
        // approved from 1 Jan; suspended and approved both recorded on 10 Mar (the later wins);
        // then cancelled from 1 May.
        var march = new DateTime(2027, 3, 10, 9, 0, 0, DateTimeKind.Utc);
        var history = StatusHistoryBuilder.Build(
            [
                ("approved", Nov2026),
                ("suspended", march),
                ("approved", march.AddHours(2)),
                ("cancelled", new DateTime(2027, 5, 1, 9, 0, 0, DateTimeKind.Utc)),
            ],
            2027
        );

        history
            .Select(e => (e.Status, e.EffectiveFromDate, e.EffectiveToDate))
            .Should()
            .Equal(
                ("approved", new DateOnly(2027, 1, 1), (DateOnly?)new DateOnly(2027, 3, 9)),
                ("suspended", new DateOnly(2027, 3, 10), (DateOnly?)new DateOnly(2027, 3, 10)),
                ("approved", new DateOnly(2027, 3, 10), (DateOnly?)new DateOnly(2027, 4, 30)),
                ("cancelled", new DateOnly(2027, 5, 1), (DateOnly?)null)
            );
    }

    [Fact]
    public void AStatusOfWithdrawn_IsKeptInTheHistory_AndEndsThePreviousStatus()
    {
        var history = StatusHistoryBuilder.Build(
            [
                ("approved", Nov2026),
                ("withdrawn", new DateTime(2027, 2, 1, 12, 0, 0, DateTimeKind.Utc)),
            ],
            2027
        );

        history.Select(e => e.Status).Should().Equal("approved", "withdrawn");
        history[0].EffectiveToDate.Should().Be(new DateOnly(2027, 1, 31));
        history[1].EffectiveToDate.Should().BeNull();
    }

    [Fact]
    public void Accreditation_CarriesNoInternalIds_OrCaseManagementDetails()
    {
        var text = JsonSerializer.Serialize(
            AccreditationApiMapper.ToAccreditation(Application()),
            Web
        );

        text.Should().NotContain("reex-internal-object-id");
        text.Should().NotContain("RA-000000042");
        text.ToLowerInvariant().Should().NotContain("workitem").And.NotContain("version");
    }

    // ---- overseas sites ----

    private static OverseasSiteModel Site(Action<OverseasSiteModel>? configure = null)
    {
        var site = new OverseasSiteModel
        {
            SiteId = 7,
            OrsId = "001",
            SiteName = "Approved Recyclers GmbH",
            SiteAddress = "Hafenstrasse 1, Hamburg, Germany",
            AddressLine1 = "Hafenstrasse 1",
            TownOrCity = "Hamburg",
            Country = "Germany",
            Coordinates = "53.5511, 9.9937",
            ContactName = "Greta Schmidt",
            ContactEmail = "greta@example.com",
            ContactPhone = "+49 40 555 0142",
            OperationCodes = ["R3"],
            Code1 = "A1181",
            RepatriatedLoads = "Returned within 30 days",
            ConditionsOfExport = true,
            IsEu = true,
            IsOecd = true,
            ValidFrom = "2026-03-14T00:00:00.000Z",
            Selected = true,
            IsNewSite = true,
            RegisteredNowAccredited = true,
            BesEvidence = new BesEvidenceModel
            {
                BesEvidenceUploads =
                [
                    new BesEvidenceFileModel
                    {
                        FileId = "f1",
                        Filename = "evidence.pdf",
                        S3Key = "private/key",
                        S3Bucket = "private-bucket",
                    },
                ],
            },
        };
        configure?.Invoke(site);
        return site;
    }

    private static JsonElement OverseasSite(
        OverseasSiteModel site,
        bool includeWithdrawn = false
    ) =>
        JsonSerializer.SerializeToElement(
            AccreditationApiMapper.ToOverseasSite(
                Application(exporter: true),
                site,
                includeWithdrawn
            ),
            Web
        );

    [Fact]
    public void OverseasSite_CarriesItsDetails_AndItsApproval()
    {
        var json = OverseasSite(Site());

        json.GetProperty("siteId").GetInt32().Should().Be(7);
        json.GetProperty("orsId").GetString().Should().Be("001");
        json.GetProperty("siteName").GetString().Should().Be("Approved Recyclers GmbH");
        json.GetProperty("formattedAddress")
            .GetString()
            .Should()
            .Be("Hafenstrasse 1, Hamburg, Germany");
        var address = json.GetProperty("address");
        address.GetProperty("line1").GetString().Should().Be("Hafenstrasse 1");
        address.GetProperty("town").GetString().Should().Be("Hamburg");
        address.GetProperty("country").GetString().Should().Be("Germany");
        json.GetProperty("coordinates").GetString().Should().Be("53.5511, 9.9937");
        json.GetProperty("operationCodes")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .Equal("R3");
        json.GetProperty("repatriatedLoads").GetString().Should().Be("Returned within 30 days");
        json.GetProperty("conditionsOfExport").GetBoolean().Should().BeTrue();
        json.GetProperty("isEu").GetBoolean().Should().BeTrue();
        json.GetProperty("isOecd").GetBoolean().Should().BeTrue();
        json.TryGetProperty("validFrom", out _).Should().BeFalse();
    }

    [Fact]
    public void OverseasSite_CarriesItsContact()
    {
        var contact = OverseasSite(Site()).GetProperty("contact");

        contact.GetProperty("fullName").GetString().Should().Be("Greta Schmidt");
        contact.GetProperty("email").GetString().Should().Be("greta@example.com");
        contact.GetProperty("phone").GetString().Should().Be("+49 40 555 0142");
    }

    [Fact]
    public void OverseasSite_WithoutAContact_HasNoContactObject()
    {
        var json = OverseasSite(
            Site(s =>
            {
                s.ContactName = null;
                s.ContactEmail = " ";
                s.ContactPhone = null;
            })
        );

        json.TryGetProperty("contact", out _).Should().BeFalse();
    }

    [Fact]
    public void OverseasSite_DropsApplicationOnlyFlagsLegacyCodesAndEvidence()
    {
        var text = JsonSerializer.Serialize(
            AccreditationApiMapper.ToOverseasSite(Application(exporter: true), Site(), false),
            Web
        );

        foreach (
            var name in new[]
            {
                "selected",
                "isNewSite",
                "registeredNowAccredited",
                "code1",
                "code2",
                "code3",
                "interimSiteIds",
            }
        )
        {
            text.Should().NotContain($"\"{name}\"");
        }

        text.ToLowerInvariant().Should().NotContain("besevidence");
        text.Should()
            .NotContain("private/key")
            .And.NotContain("private-bucket")
            .And.NotContain("evidence.pdf");
    }

    [Fact]
    public void OverseasSite_StatusHistory_IsRecordedWhenTheApplicationWasApproved_NotOnItsValidFrom()
    {
        // The site's stored validFrom (14 March 2026) is not served, and does not date the status.
        var json = OverseasSite(Site());

        json.GetProperty("latestStatus").GetProperty("status").GetString().Should().Be("approved");
        var entry = json.GetProperty("statusHistory").EnumerateArray().Single();
        entry.GetProperty("status").GetString().Should().Be("approved");
        entry.GetProperty("effectiveFromDate").GetString().Should().Be("2027-01-10");
        entry.GetProperty("effectiveToDate").ValueKind.Should().Be(JsonValueKind.Null);
        entry.GetProperty("recordedAt").GetDateTime().Should().Be(ApprovedAt);
    }

    [Fact]
    public void OverseasSite_WithNoApprovalDate_TakesEffectWhenTheAccreditationWasApproved()
    {
        var json = OverseasSite(Site(s => s.ValidFrom = null));

        json.TryGetProperty("validFrom", out _).Should().BeFalse();
        json.GetProperty("statusHistory")[0]
            .GetProperty("effectiveFromDate")
            .GetString()
            .Should()
            .Be("2027-01-10");
    }

    [Fact]
    public void OverseasSite_WithUnparseableValidFrom_IsTreatedAsAbsent()
    {
        var json = OverseasSite(Site(s => s.ValidFrom = "not a date"));

        json.TryGetProperty("validFrom", out _).Should().BeFalse();
        json.GetProperty("statusHistory")[0]
            .GetProperty("effectiveFromDate")
            .GetString()
            .Should()
            .Be("2027-01-10");
    }

    [Fact]
    public void OverseasSite_ListsOnlyItsActiveInterimSites_InFull()
    {
        var json = OverseasSite(
            Site(s =>
                s.InterimSites = [
                    InterimSite(11, removed: false),
                    InterimSite(12, removed: true),
                    InterimSite(10, removed: false),
                ]
            )
        );

        var interims = json.GetProperty("interimSites").EnumerateArray().ToList();
        interims.Select(i => i.GetProperty("siteId").GetInt32()).Should().Equal(10, 11);
        interims[0].GetProperty("siteNumber").GetString().Should().Be("010");
        interims[0].GetProperty("siteName").GetString().Should().Be("Interim 10");
        interims[0]
            .GetProperty("operationCodes")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .Equal("R12");
        interims[0].GetProperty("createdAt").GetDateTime().Should().Be(InterimCreated);
        interims[0].TryGetProperty("removedAt", out _).Should().BeFalse();
        interims[0].TryGetProperty("isNewSite", out _).Should().BeFalse();
    }

    [Fact]
    public void OverseasSite_IncludesWithdrawnInterimSites_WhenAskedFor()
    {
        var json = OverseasSite(
            Site(s =>
                s.InterimSites = [InterimSite(11, removed: false), InterimSite(12, removed: true)]
            ),
            includeWithdrawn: true
        );

        var interims = json.GetProperty("interimSites").EnumerateArray().ToList();
        interims.Select(i => i.GetProperty("siteId").GetInt32()).Should().Equal(11, 12);
        interims[0].TryGetProperty("removedAt", out _).Should().BeFalse();
        interims[1].GetProperty("removedAt").GetDateTime().Should().Be(InterimRemoved);
    }

    [Fact]
    public void InterimSite_CarriesItsAddressAndContact()
    {
        var json = OverseasSite(Site(s => s.InterimSites = [InterimSite(11, removed: false)]));

        var interim = json.GetProperty("interimSites")[0];
        var address = interim.GetProperty("address");
        address.GetProperty("line1").GetString().Should().Be("Line 1");
        address.GetProperty("line2").GetString().Should().Be("Line 2");
        address.GetProperty("town").GetString().Should().Be("Town");
        address.GetProperty("region").GetString().Should().Be("Region");
        address.GetProperty("country").GetString().Should().Be("Germany");
        address.GetProperty("postcode").GetString().Should().Be("20457");
        var contact = interim.GetProperty("contact");
        contact.GetProperty("fullName").GetString().Should().Be("Interim Contact");
        contact.GetProperty("email").GetString().Should().Be("interim@example.com");
        contact.GetProperty("phone").GetString().Should().Be("+49 1");
    }

    [Fact]
    public void OverseasSite_WithoutAnAddress_HasNoAddressObject()
    {
        var json = OverseasSite(
            Site(s =>
            {
                s.AddressLine1 = null;
                s.AddressLine2 = null;
                s.TownOrCity = null;
                s.Country = null;
            })
        );

        json.TryGetProperty("address", out _).Should().BeFalse();
    }

    private static readonly DateTime InterimCreated = new(2027, 2, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime InterimRemoved = new(2027, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    private static InterimSiteModel InterimSite(int id, bool removed) =>
        new()
        {
            SiteId = id,
            SiteNumber = id.ToString("000"),
            Country = "Germany",
            SiteName = $"Interim {id}",
            AddressLine1 = "Line 1",
            AddressLine2 = "Line 2",
            TownOrCity = "Town",
            StateOrRegion = "Region",
            Postcode = "20457",
            ContactName = "Interim Contact",
            ContactEmail = "interim@example.com",
            ContactPhone = "+49 1",
            OperationCodes = ["R12"],
            CreatedAt = InterimCreated,
            RemovedAt = removed ? InterimRemoved : null,
        };

    // ---- latestStatus ----

    private static void AssertLatestStatusIsTheLastHistoryEntry(JsonElement json)
    {
        json.TryGetProperty("status", out _).Should().BeFalse();
        var latest = json.GetProperty("latestStatus");
        foreach (var field in new[] { "status", "effectiveFromDate", "effectiveToDate", "recordedAt" })
            latest.TryGetProperty(field, out _).Should().BeTrue($"latestStatus has {field}");
        latest.GetRawText().Should().Be(json.GetProperty("statusHistory").EnumerateArray().Last().GetRawText());
    }

    [Fact]
    public void Accreditation_HasALatestStatus_IdenticalToTheLatestHistoryEntry()
    {
        AssertLatestStatusIsTheLastHistoryEntry(Accreditation(Application()));
    }

    [Fact]
    public void OverseasSite_HasALatestStatus_IdenticalToTheLatestHistoryEntry()
    {
        AssertLatestStatusIsTheLastHistoryEntry(OverseasSite(Site()));
    }

    [Fact]
    public void Summary_HasALatestStatus_AndNoStatusString()
    {
        var json = Summary(Application());

        json.TryGetProperty("status", out _).Should().BeFalse();
        var latest = json.GetProperty("latestStatus");
        latest.GetProperty("status").GetString().Should().Be("approved");
        latest.GetProperty("effectiveFromDate").GetString().Should().Be("2027-01-10");
        latest.GetProperty("effectiveToDate").ValueKind.Should().Be(JsonValueKind.Null);
        latest.GetProperty("recordedAt").GetDateTime().Should().Be(ApprovedAt);
        json.TryGetProperty("statusHistory", out _).Should().BeFalse();
    }

    // A 2027 accreditation approved in November 2026: the status does not apply until 1 January 2027,
    // but it is still the latest status. The effective dates are how a client tells.
    [Fact]
    public void LatestStatus_CanBeFutureDated_AndIsStillTheLatest()
    {
        var json = Accreditation(
            Application(configure: a => a.CaseManagementStatusUpdatedAt = Nov2026)
        );

        var latest = json.GetProperty("latestStatus");
        latest.GetProperty("status").GetString().Should().Be("approved");
        latest.GetProperty("effectiveFromDate").GetString().Should().Be("2027-01-01");
        AssertLatestStatusIsTheLastHistoryEntry(json);
    }

    private static StatusHistoryEntryDto Entry(string status, string from, DateTime recordedAt) =>
        new()
        {
            Status = status,
            EffectiveFromDate = DateOnly.Parse(from),
            EffectiveToDate = null,
            RecordedAt = recordedAt,
        };

    // Not a history from Build: the latest entry is first, so a plain Last() would not find it.
    [Fact]
    public void Latest_IsTheLatestFromDate_WhateverTheInputOrder()
    {
        var latestFirst = Entry("approved", "2099-05-01", Nov2026);
        var history = new List<StatusHistoryEntryDto>
        {
            latestFirst,
            Entry("approved", "2027-01-01", Nov2026),
            Entry("suspended", "2027-03-10", Nov2026.AddDays(5)),
        };

        StatusHistoryBuilder.Latest(history).Should().BeSameAs(latestFirst);
    }

    // The later-recorded entry is in the middle and the earlier-recorded one is last, so neither Last()
    // nor a sort by effectiveFromDate alone finds it.
    [Fact]
    public void Latest_WhereTheTwoLatestShareAFromDate_IsTheLaterRecorded_WhateverTheInputOrder()
    {
        var laterRecorded = Entry("suspended", "2027-03-10", Nov2026.AddDays(5));
        var history = new List<StatusHistoryEntryDto>
        {
            Entry("approved", "2027-01-01", Nov2026),
            laterRecorded,
            Entry("approved", "2027-03-10", Nov2026),
        };

        StatusHistoryBuilder.Latest(history).Should().BeSameAs(laterRecorded);
    }
}
