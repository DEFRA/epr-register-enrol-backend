namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// RA-706: <c>DomainModel:LegacyWrites</c> off is the cut. The host refuses to start without its prerequisites, in
/// Development too (these hosts run as Development): a frozen legacy read is wrong in every environment.
/// </summary>
public sealed partial class DualWriteEndpointTests
{
    [Fact]
    public void LegacyWritesOffWithoutReadFromModel_InDevelopment_RefusesToStart()
    {
        var cut = new Dictionary<string, string?>(s_dualWriteOn)
        {
            ["DomainModel:DualWriteSeed"] = "true",
            ["DomainModel:LegacyWrites"] = "false",
        };

        var ex = Assert.Throws<InvalidOperationException>(() => Start(cut));

        Assert.StartsWith("DomainModel:LegacyWrites off needs DomainModel:ReadFromModel", ex.Message);
    }
}
