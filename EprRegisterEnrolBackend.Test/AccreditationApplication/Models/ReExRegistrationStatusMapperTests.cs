using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Models;

public class ReExRegistrationStatusMapperTests
{
    // RA-806: ReEx's REGISTRATION_STATUS values (epr-backend), by name, ignoring case.
    [Theory]
    [InlineData("created", RegistrationStatus.Created)]
    [InlineData("approved", RegistrationStatus.Approved)]
    [InlineData("rejected", RegistrationStatus.Rejected)]
    [InlineData("cancelled", RegistrationStatus.Cancelled)]
    [InlineData("APPROVED", RegistrationStatus.Approved)]
    [InlineData("Cancelled", RegistrationStatus.Cancelled)]
    public void Map_ReExRegistrationStatus_MapsToTheSameStatus(
        string reExStatus,
        RegistrationStatus expected
    )
    {
        Assert.Equal(expected, ReExRegistrationStatusMapper.Map(reExStatus));
    }

    // Missing, blank, not a registration status (suspended and active are accreditation and organisation statuses),
    // the enum's number, or a name with padding: none is a status ReEx sends, so none is guessed at.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("suspended")]
    [InlineData("active")]
    [InlineData("1")]
    [InlineData(" approved")]
    public void Map_NotAReExRegistrationStatus_IsNull(string? reExStatus)
    {
        Assert.Null(ReExRegistrationStatusMapper.Map(reExStatus));
    }
}
