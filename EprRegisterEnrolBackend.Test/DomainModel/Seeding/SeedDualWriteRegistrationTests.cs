using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolBackend.Test.DomainModel.Seeding;

public class SeedDualWriteRegistrationTests
{
    private static ServiceCollection Register(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddDomainModel(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services;
    }

    private static Type? SeedWriter(ServiceCollection services) =>
        services.Single(d => d.ServiceType == typeof(ISeedDualWriter)).ImplementationType;

    [Fact]
    public void AddDomainModel_FlagOff_RegistersTheNoOpWriter()
    {
        var services = Register(new() { ["DomainModel:ExpandEnabled"] = "true" });

        Assert.Equal(typeof(NoOpSeedDualWriter), SeedWriter(services));
    }

    [Fact]
    public void AddDomainModel_FlagOnWithExpand_RegistersTheSeedDualWriter()
    {
        var services = Register(
            new() { ["DomainModel:ExpandEnabled"] = "true", ["DomainModel:DualWriteSeed"] = "true" }
        );

        Assert.Equal(typeof(SeedDualWriter), SeedWriter(services));
        Assert.Contains(services, d => d.ServiceType == typeof(SeedModelStores));
        Assert.Contains(services, d => d.ServiceType == typeof(SeedDualWriteMetrics));
    }

    [Fact]
    public void AddDomainModel_FlagOnWithoutExpand_RefusesToStart()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Register(new() { ["DomainModel:DualWriteSeed"] = "true" })
        );

        Assert.Contains("DomainModel:ExpandEnabled", ex.Message);
    }
}
