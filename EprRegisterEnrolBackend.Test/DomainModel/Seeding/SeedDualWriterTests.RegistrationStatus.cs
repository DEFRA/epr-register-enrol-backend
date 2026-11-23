using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;

namespace EprRegisterEnrolBackend.Test.DomainModel.Seeding;

// RA-806: the seed writes ReEx's registration status, and a later seed that finds it changed writes a new version,
// like any other ReEx field (ReExRefresh). The migrator's ensure-migrated step runs in the same seed and keeps it.
// A missing or unknown ReEx status never reaches the writer: GetAccreditationAsync fails, so the seed does.
public sealed partial class SeedDualWriterTests
{
    private static ReExAccreditationDto ReExWithStatus(RegistrationStatus status)
    {
        var reEx = ReEx();
        reEx.RegistrationStatus = status;
        return reEx;
    }

    [Fact]
    public async Task WriteAsync_ReExRegistrationApproved_WritesTheRegistrationApproved()
    {
        await SeedAsync(ReExWithStatus(RegistrationStatus.Approved));

        var registration = Assert.Single(await RegistrationVersionsAsync());
        Assert.Equal(RegistrationStatus.Approved, registration.Status);
        Assert.Equal(ChangeReason.ReExImport, registration.LastChange.Reason);
    }

    [Fact]
    public async Task WriteAsync_RegistrationApprovedInReExSinceTheLastSeed_WritesANewApprovedVersion()
    {
        await SeedAsync(ReExWithStatus(RegistrationStatus.Created));

        _clock.Advance(TimeSpan.FromMinutes(5));
        await SeedAsync(ReExWithStatus(RegistrationStatus.Approved));

        var versions = await RegistrationVersionsAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal(RegistrationStatus.Created, versions.Single(r => !r.IsCurrent).Status);
        var current = versions.Single(r => r.IsCurrent);
        Assert.Equal(2, current.Version);
        Assert.Equal(RegistrationStatus.Approved, current.Status);
        Assert.Equal(ChangeReason.ReExRefresh, current.LastChange.Reason);
    }

    [Fact]
    public async Task WriteAsync_SameReExRegistrationStatusAgain_WritesNoNewVersion()
    {
        await SeedAsync(ReExWithStatus(RegistrationStatus.Approved));

        _clock.Advance(TimeSpan.FromMinutes(5));
        await SeedAsync(ReExWithStatus(RegistrationStatus.Approved));

        var registration = Assert.Single(await RegistrationVersionsAsync());
        Assert.Equal(RegistrationStatus.Approved, registration.Status);
    }
}
