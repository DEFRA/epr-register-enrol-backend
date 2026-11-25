using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Seeding;

namespace EprRegisterEnrolBackend.Test.DomainModel.Seeding;

// RA-806: a Registration's status is ReEx's, copied at seed onto a new entity and the current version alike. It is not
// checked against VALID_REG_TRANSITIONS: ReEx masters it (design doc §3.4).
public partial class SeedEntityMapperTests
{
    [Theory]
    [InlineData(RegistrationStatus.Created)]
    [InlineData(RegistrationStatus.Approved)]
    [InlineData(RegistrationStatus.Rejected)]
    [InlineData(RegistrationStatus.Cancelled)]
    public void ApplyToRegistration_NewEntity_TakesReExsStatus(RegistrationStatus reExStatus)
    {
        var target = new RegistrationEntity();
        var dto = Dto();
        dto.RegistrationStatus = reExStatus;

        SeedEntityMapper.ApplyTo(target, dto, "reg-1", MaterialType.Plastic);

        Assert.Equal(reExStatus, target.Status);
    }

    [Theory]
    [InlineData(RegistrationStatus.Created, RegistrationStatus.Approved)]
    [InlineData(RegistrationStatus.Approved, RegistrationStatus.Cancelled)]
    [InlineData(RegistrationStatus.Approved, RegistrationStatus.Created)]
    public void ApplyToRegistration_HeldVersion_TakesReExsStatus(
        RegistrationStatus held,
        RegistrationStatus reExStatus
    )
    {
        var target = new RegistrationEntity { Status = held };
        var dto = Dto();
        dto.RegistrationStatus = reExStatus;

        SeedEntityMapper.ApplyTo(target, dto, "reg-1", MaterialType.Plastic);

        Assert.Equal(reExStatus, target.Status);
    }
}
