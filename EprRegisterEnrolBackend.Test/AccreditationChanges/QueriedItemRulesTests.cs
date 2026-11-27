using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationChanges;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

/// <summary>RA-698: unit tests of the queried-item rules and the query details on the change request view.</summary>
public class QueriedItemRulesTests
{
    private static readonly string[] OperationCodes = ["R3"];
    private static readonly string[] BaselCodes = ["B1010"];

    private static InterimSiteModel Interim(int siteId, DateTime? removedAt = null) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = $"00{siteId}",
            Country = "France",
            SiteName = "Interim",
            AddressLine1 = "2 Rue",
            TownOrCity = "Lyon",
            ContactName = "Contact",
            ContactEmail = "c@example.com",
            ContactPhone = "0200",
            RemovedAt = removedAt,
        };

    private static OverseasSiteModel Site() =>
        new()
        {
            SiteId = 1,
            OrsId = "001",
            SiteName = "Site",
            AddressLine1 = "1 Rue",
            TownOrCity = "Paris",
            Country = "France",
            ContactName = "Contact",
            ContactEmail = "c@example.com",
            ContactPhone = "0100",
            RepatriatedLoads = "Returned.",
        };

    [Fact]
    public void ActiveInterimSites_NoSite_IsEmpty() =>
        QueriedItemRules.ActiveInterimSites(null).Should().BeEmpty();

    [Fact]
    public void ActiveInterimSites_ListWins_AndWithdrawnSitesAreLeftOut()
    {
        var site = Site();
        site.InterimSites = [Interim(1), Interim(2, DateTime.UtcNow)];
        site.InterimSite = Interim(9);

        QueriedItemRules.ActiveInterimSites(site).Select(s => s.SiteId).Should().Equal(1);
    }

    [Fact]
    public void ActiveInterimSites_FallsBackToTheSingularMirror()
    {
        var site = Site();
        site.InterimSite = Interim(9);

        QueriedItemRules.ActiveInterimSites(site).Select(s => s.SiteId).Should().Equal(9);
    }

    [Fact]
    public void ActiveInterimSites_NoInterimSites_IsEmpty() =>
        QueriedItemRules.ActiveInterimSites(Site()).Should().BeEmpty();

    [Fact]
    public void Apply_OrsAnswer_KeepsChangesAndDropsReverts()
    {
        var site = Site();
        var answers = new ChangeRequestSiteAnswers { AddressLine1 = "stale" };

        QueriedItemRules.Apply(
            answers,
            new PatchQueriedSiteRequest { SiteName = "  New  ", AddressLine1 = "1 Rue", Postcode = "75001" },
            site
        );

        answers.SiteName.Should().Be("New");
        answers.AddressLine1.Should().BeNull();
        answers.Postcode.Should().Be("75001");
        answers.TownOrCity.Should().BeNull();
    }

    [Fact]
    public void Apply_InterimAnswer_KeepsChangesAndDropsReverts()
    {
        var interim = Interim(1);
        var answers = new ChangeRequestInterimSiteAnswers { SiteId = 1, SiteName = "stale" };

        QueriedItemRules.Apply(
            answers,
            new PatchQueriedSiteRequest { SiteName = "Interim", TownOrCity = "Nice" },
            interim
        );

        answers.SiteName.Should().BeNull();
        answers.TownOrCity.Should().Be("Nice");
    }

    [Fact]
    public void LockedViolations_NothingLocked_IsEmpty() =>
        QueriedItemRules.LockedViolations(new PatchQueriedSiteRequest { SiteName = "x" }, interim: false)
            .Should().BeEmpty();

    [Fact]
    public void LockedViolations_ListsEveryLockedFieldSupplied()
    {
        var body = new PatchQueriedSiteRequest
        {
            Country = "FR",
            OperationCodes = [.. OperationCodes],
            Coordinates = "1,2",
            BaselCodes = [.. BaselCodes],
            ConditionsOfExport = true,
            RejectedLoadsArrangements = "x",
        };

        QueriedItemRules.LockedViolations(body, interim: false).Keys.Should().BeEquivalentTo(
            ChangeRequestRules.Country,
            ChangeRequestRules.OperationCodes,
            ChangeRequestRules.Coordinates,
            QueriedItemRules.BaselCodes,
            ChangeRequestRules.ConditionsOfExport
        );
        QueriedItemRules.LockedViolations(body, interim: true).Keys.Should().Contain(
            QueriedItemRules.RejectedLoadsArrangements
        );
    }

    [Fact]
    public void ChangedFields_ComparesWithTheAnswersAtQuery()
    {
        var current = new ChangeRequestSiteAnswers
        {
            SiteName = "New",
            Postcode = "75001",
            InterimSites = [new ChangeRequestInterimSiteAnswers { SiteId = 3, TownOrCity = "Nice" }],
        };
        var atQuery = new ChangeRequestSiteAnswers { Postcode = "75001" };

        QueriedItemRules.ChangedFields(current, atQuery).Should().BeEquivalentTo(
            QueriedItemRules.SiteName,
            QueriedItemRules.InterimKey(3, QueriedItemRules.TownOrCity)
        );
        QueriedItemRules.ChangedFields(new ChangeRequestSiteAnswers { Postcode = "1" }, null)
            .Should().Equal(QueriedItemRules.Postcode);
    }

    [Fact]
    public void ChangedFields_AnAnswerUndoneSinceTheQuery_IsAChange()
    {
        var atQuery = new ChangeRequestSiteAnswers
        {
            ContactName = "B",
            InterimSites = [new ChangeRequestInterimSiteAnswers { SiteId = 3, ContactName = "C" }],
        };

        QueriedItemRules.ChangedFields(new ChangeRequestSiteAnswers(), atQuery).Should().BeEquivalentTo(
            QueriedItemRules.ContactName,
            QueriedItemRules.InterimKey(3, QueriedItemRules.ContactName)
        );
    }

    [Fact]
    public void NewBesFiles_AreThoseNotHeldAtTheQuery()
    {
        BesEvidenceFileModel File(string id) => new() { FileId = id, Filename = id, S3Key = id };
        var item = new ChangeRequestSite
        {
            SiteId = 1,
            SiteName = "Site",
            BesEvidence = new BesEvidenceModel { BesEvidenceUploads = [File("a"), File("b")] },
        };

        QueriedItemRules.NewBesFiles(item).Select(f => f.FileId).Should().Equal("a", "b");

        item.Query = new ItemQuery { QueryNote = "n", BesFileIdsAtQuery = ["a"] };
        QueriedItemRules.NewBesFiles(item).Select(f => f.FileId).Should().Equal("b");

        item.BesEvidence = null;
        QueriedItemRules.NewBesFiles(item).Should().BeEmpty();
    }

    [Fact]
    public void Clone_IsADeepCopy()
    {
        var answers = new ChangeRequestSiteAnswers
        {
            SiteName = "x",
            InterimSites = [new ChangeRequestInterimSiteAnswers { SiteId = 1, SiteName = "y" }],
        };

        var clone = QueriedItemRules.Clone(answers);
        clone.InterimSites[0].SiteName = "changed";

        answers.InterimSites[0].SiteName.Should().Be("y");
        clone.SiteName.Should().Be("x");
    }

    [Fact]
    public void Effective_CarriesTheOperatorsChangesOverTheOrsValues()
    {
        var answers = new ChangeRequestSiteAnswers
        {
            SiteName = "New",
            StateOrRegion = "PACA",
            RejectedLoadsArrangements = "New arrangements",
            InterimSites = [new ChangeRequestInterimSiteAnswers { SiteId = 1 }],
        };

        var effective = ChangeRequestRules.Effective(Site(), answers);

        effective.SiteName.Should().Be("New");
        effective.AddressLine1.Should().Be("1 Rue");
        effective.StateOrRegion.Should().Be("PACA");
        effective.RejectedLoadsArrangements.Should().Be("New arrangements");
        effective.InterimSites.Should().HaveCount(1);
        ChangeRequestRules.Effective(Site(), new ChangeRequestSiteAnswers()).RejectedLoadsArrangements
            .Should().Be("Returned.");
        ChangeRequestRules.Effective(null, new ChangeRequestSiteAnswers()).SiteName.Should().BeNull();
    }

    private static (AccreditationChangeRequest request, AccreditationApplicationModel accreditation) Request(
        ChangeItemStatus status,
        bool queried
    )
    {
        var site = Site();
        site.InterimSites = [Interim(5), Interim(6, DateTime.UtcNow)];
        var item = new ChangeRequestSite
        {
            SiteId = 1,
            OrsId = "001",
            SiteName = "Site",
            Status = status,
            Answers = new ChangeRequestSiteAnswers
            {
                InterimSites = [new ChangeRequestInterimSiteAnswers { SiteId = 5, TownOrCity = "Nice" }],
            },
        };
        if (queried)
            item.Query = new ItemQuery { QueryNote = "Please confirm", BesFileIdsAtQuery = ["f1"] };
        var request = new AccreditationChangeRequest
        {
            OrganisationId = "org",
            ApplicationId = "app",
            Year = 2026,
            MaterialType = MaterialType.Steel,
            Sites = [item],
        };
        var accreditation = new AccreditationApplicationModel
        {
            OrganisationId = "org",
            OrganisationName = "Acme",
            Year = 2026,
            MaterialType = MaterialType.Steel,
            ApplicationStatus = ApplicationStatus.Approved,
            OverseasSites = new AccreditationApplicationOverseasSites { Sites = [site] },
        };
        return (request, accreditation);
    }

    [Fact]
    public void ToView_QueriedItem_ListsEditableFieldsAndEffectiveInterimValues()
    {
        var (request, accreditation) = Request(ChangeItemStatus.Queried, queried: true);

        var query = ChangeRequestRules.ToView(request, accreditation).Sites.Single().Query;

        query.Should().NotBeNull();
        query!.QueryNote.Should().Be("Please confirm");
        query.PreQueryBesFileIds.Should().Equal("f1");
        query.EditableFields.Should().Contain(QueriedItemRules.SiteName);
        query.LockedFields.Should().Contain(ChangeRequestRules.Country);
        var interim = query.InterimSites.Single();
        interim.SiteId.Should().Be(5);
        interim.Values[QueriedItemRules.TownOrCity].Should().Be("Nice");
        interim.Values[QueriedItemRules.SiteName].Should().Be("Interim");
        interim.EditableFields.Should().NotBeEmpty();
        interim.LockedFields.Should().NotBeEmpty();
    }

    [Fact]
    public void ToView_AnsweredQuery_HasNothingLeftToEdit()
    {
        var (request, accreditation) = Request(ChangeItemStatus.Updated, queried: true);

        var query = ChangeRequestRules.ToView(request, accreditation).Sites.Single().Query;

        query.Should().NotBeNull();
        query!.EditableFields.Should().BeEmpty();
        query.LockedFields.Should().BeEmpty();
        query.InterimSites.Should().BeEmpty();
    }

    [Fact]
    public void ToView_ItemNeverQueried_HasNoQuery()
    {
        var (request, accreditation) = Request(ChangeItemStatus.Submitted, queried: false);

        ChangeRequestRules.ToView(request, accreditation).Sites.Single().Query.Should().BeNull();
    }
}
