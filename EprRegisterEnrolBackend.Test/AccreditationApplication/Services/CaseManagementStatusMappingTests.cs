using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

public class CaseManagementStatusMappingTests
{
    [Theory]
    [InlineData("duly-made", ApplicationStatus.DulyMade)]
    [InlineData("assessment-in-progress", ApplicationStatus.Updated)]
    [InlineData("approved", ApplicationStatus.Approved)]
    public void ForStatusPush_MapsAListedState(string toStateId, ApplicationStatus expected) =>
        CaseManagementStatusMapping.ForStatusPush(toStateId).Should().Be(expected);

    // A state with no entry is the documented no-op (RA-368 §4.3), not an error.
    [Fact]
    public void ForStatusPush_StateWithNoEntry_IsNull() =>
        CaseManagementStatusMapping.ForStatusPush("unmapped-future-state").Should().BeNull();

    // The receivers refuse a blank toStateId with a 400 first (RejectMissingToStateId), so one reaching the
    // mapping is a bug: it throws rather than reading as "no entry".
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForStatusPush_BlankToStateId_Throws(string? toStateId)
    {
        var map = () => CaseManagementStatusMapping.ForStatusPush(toStateId!);

        map.Should().Throw<ArgumentException>();
    }

    // A known-ignored state sets no status by design, so it must not also be in the table.
    [Fact]
    public void KnownIgnoredStateIds_AreNotInTheTable()
    {
        CaseManagementStatusMapping.KnownIgnoredStateIds.Should().Equal("queried");
        foreach (var stateId in CaseManagementStatusMapping.KnownIgnoredStateIds)
            CaseManagementStatusMapping.ForStatusPush(stateId).Should().BeNull();
    }
}
