using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Seeding;

namespace EprRegisterEnrolBackend.Test.DomainModel.Seeding;

// RA-806: a Registration's status is ReEx's, copied at seed onto a new entity and the current version alike.
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

    [Fact]
    public void ApplyToRegistration_HeldCreatedReExApproved_BecomesApproved()
    {
        var target = new RegistrationEntity { Status = RegistrationStatus.Created };
        var dto = Dto();
        dto.RegistrationStatus = RegistrationStatus.Approved;

        SeedEntityMapper.ApplyTo(target, dto, "reg-1", MaterialType.Plastic);

        Assert.Equal(RegistrationStatus.Approved, target.Status);
    }

    // ReEx sent no status it has (the adapter logged it at Error): the held one stays, so a new Registration stays
    // Created, which refuses change requests.
    [Fact]
    public void ApplyToRegistration_NoReExStatus_NewEntityStaysCreated()
    {
        var target = new RegistrationEntity();

        SeedEntityMapper.ApplyTo(target, Dto(), "reg-1", MaterialType.Plastic);

        Assert.Equal(RegistrationStatus.Created, target.Status);
    }

    [Fact]
    public void ApplyToRegistration_NoReExStatus_KeepsTheHeldStatus()
    {
        var target = new RegistrationEntity { Status = RegistrationStatus.Approved };

        SeedEntityMapper.ApplyTo(target, Dto(), "reg-1", MaterialType.Plastic);

        Assert.Equal(RegistrationStatus.Approved, target.Status);
    }
}
