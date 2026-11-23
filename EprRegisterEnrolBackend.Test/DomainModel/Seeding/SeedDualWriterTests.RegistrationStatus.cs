using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;

namespace EprRegisterEnrolBackend.Test.DomainModel.Seeding;

// RA-806: the seed writes ReEx's registration status, and a later seed that finds it changed writes a new version,
// like any other ReEx field (ReExRefresh). The migrator's ensure-migrated step runs in the same seed and keeps it.
public sealed partial class SeedDualWriterTests
{
    private static ReExAccreditationDto ReExWithStatus(RegistrationStatus? status)
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

    // The application being started does not depend on the registration's status, so a seed without one still writes
    // the model; the Registration stays Created, which refuses change requests.
    [Fact]
    public async Task WriteAsync_NoReExRegistrationStatus_WritesTheModelWithTheRegistrationCreated()
    {
        await SeedAsync(ReExWithStatus(null));

        var registration = Assert.Single(await RegistrationVersionsAsync());
        Assert.Equal(RegistrationStatus.Created, registration.Status);
        Assert.Equal(0, Interlocked.Read(ref _failures));
    }

    [Fact]
    public async Task WriteAsync_NoReExRegistrationStatus_KeepsTheHeldStatusAndWritesNoNewVersion()
    {
        await SeedAsync(ReExWithStatus(RegistrationStatus.Approved));

        _clock.Advance(TimeSpan.FromMinutes(5));
        await SeedAsync(ReExWithStatus(null));

        var registration = Assert.Single(await RegistrationVersionsAsync());
        Assert.Equal(RegistrationStatus.Approved, registration.Status);
    }
}
