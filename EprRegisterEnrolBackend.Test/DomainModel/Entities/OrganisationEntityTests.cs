using EprRegisterEnrolBackend.DomainModel.Entities;

namespace EprRegisterEnrolBackend.Test.DomainModel.Entities;

public class OrganisationEntityTests
{
    private static OrganisationEntity Organisation(int? organisationNumber) =>
        new() { ReExOrganisationId = "org-1", OrganisationNumber = organisationNumber };

    [Fact]
    public void FillOrganisationNumber_NoneHeld_TakesIt()
    {
        var organisation = Organisation(null);

        Assert.True(organisation.FillOrganisationNumber(520458));
        Assert.Equal(520458, organisation.OrganisationNumber);
    }

    [Fact]
    public void FillOrganisationNumber_OneAlreadyHeld_KeepsIt()
    {
        var organisation = Organisation(520458);

        Assert.False(organisation.FillOrganisationNumber(999999));
        Assert.Equal(520458, organisation.OrganisationNumber);
    }

    [Fact]
    public void FillOrganisationNumber_NoNumberGiven_ChangesNothing()
    {
        var organisation = Organisation(null);

        Assert.False(organisation.FillOrganisationNumber(null));
        Assert.Null(organisation.OrganisationNumber);
    }
}
