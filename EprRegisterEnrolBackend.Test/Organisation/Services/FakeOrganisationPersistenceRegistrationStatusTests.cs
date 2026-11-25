using EprRegisterEnrolBackend.DevSeed;
using EprRegisterEnrolBackend.Organisation.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.Organisation.Services;

// RA-806: the fixture registrations carry ReEx's registration status for the stub to pass to the seed.
public class FakeOrganisationPersistenceRegistrationStatusTests
{
    [Fact]
    public async Task EveryFixtureRegistration_ButOrg50024s_IsApproved()
    {
        var sut = new FakeOrganisationPersistence(TimeProvider.System, includeDevExporters: true);
        var statuses = new List<(int OrgId, string Status)>();
        foreach (var summary in await sut.GetAllAsync())
        {
            var org = await sut.GetByOrgIdAsync(summary.OrgId);
            statuses.AddRange((org!.Registrations ?? []).Select(r => (summary.OrgId, r.Status)));
        }

        statuses.Should().ContainSingle(s => s.Status == "created").Which.OrgId.Should().Be(50024);
        statuses.Where(s => s.OrgId != 50024).Should().OnlyContain(s => s.Status == "approved");
    }

    // Only the seed dual-write may write their Registrations, so no dev seed knows them.
    [Theory]
    [InlineData(50023)]
    [InlineData(50024)]
    public void RegistrationStatusOrgs_AreNotDevSeeded(int orgId)
    {
        DevExporterOrg.Find(orgId).Should().BeNull();
    }
}
