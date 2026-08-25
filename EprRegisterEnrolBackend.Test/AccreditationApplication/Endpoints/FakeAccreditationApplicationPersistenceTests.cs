using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-482: FakeAccreditationApplicationPersistence stands in for the real persistence layer in
// every endpoint test, so its UpdateIfOrsIdAbsentAsync must model the same OrsId-absence guard
// the Mongo filter enforces -- otherwise those endpoint tests are asserting against a fake that
// has quietly stopped behaving like the thing it replaces. The endpoint-level conflict tests
// (AddOverseasSite_WhenOrsIdWriteConflictsOnce_..., ...KeepsConflicting_...) can't cover this:
// they force the conflict via the FailNextOrsIdWrites counter, which returns null before reaching
// the alreadyPresent check, leaving the collision branch itself unexercised. These tests drive the
// fake directly so that branch is genuinely covered.
public class FakeAccreditationApplicationPersistenceTests
{
    private static AccreditationApplicationModel ApplicationWithSites(
        ObjectId id,
        params string[] orsIds
    ) =>
        new()
        {
            Id = id,
            OrganisationId = "org-1",
            Year = 2026,
            MaterialType = MaterialType.Steel,
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = orsIds
                    .Select(
                        (orsId, i) =>
                            new OverseasSiteModel
                            {
                                SiteId = i + 1,
                                OrsId = orsId,
                                SiteName = $"Site {i + 1}",
                            }
                    )
                    .ToList(),
            },
        };

    [Fact]
    public async Task UpdateIfOrsIdAbsentAsync_StoredRecordAlreadyHoldsThatOrsId_ReturnsNullWithoutMutatingStore()
    {
        var sut = new FakeAccreditationApplicationPersistence();
        var id = ObjectId.GenerateNewId();
        sut.Seed(ApplicationWithSites(id, "001"));

        // A second writer that read before the first one's write landed: it computed 001 too, and
        // is now trying to persist its own copy of the document carrying a second site on 001.
        var conflictingWrite = ApplicationWithSites(id, "001", "001");

        var result = await sut.UpdateIfOrsIdAbsentAsync(conflictingWrite, "001");

        result.Should().BeNull("the id was already claimed, so this write must not persist");

        var stored = await sut.GetByIdAsync("org-1", id.ToString());
        stored!.OverseasSites!.Sites.Should().ContainSingle().Which.OrsId.Should().Be("001");
    }

    [Fact]
    public async Task UpdateIfOrsIdAbsentAsync_StoredRecordDoesNotHoldThatOrsId_PersistsTheWrite()
    {
        var sut = new FakeAccreditationApplicationPersistence();
        var id = ObjectId.GenerateNewId();
        sut.Seed(ApplicationWithSites(id, "001"));

        var write = ApplicationWithSites(id, "001", "002");

        var result = await sut.UpdateIfOrsIdAbsentAsync(write, "002");

        result.Should().BeSameAs(write);

        var stored = await sut.GetByIdAsync("org-1", id.ToString());
        stored!.OverseasSites!.Sites.Select(s => s.OrsId).Should().Equal("001", "002");
    }
}
