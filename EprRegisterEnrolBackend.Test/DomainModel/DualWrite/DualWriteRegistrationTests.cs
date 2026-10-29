using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolBackend.Test.DomainModel.DualWrite;

public class DualWriteRegistrationTests
{
    private static ServiceCollection Register(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddDomainModel(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services;
    }

    private static Type? UnitOfWork(ServiceCollection services) =>
        services.Single(d => d.ServiceType == typeof(IDualWriteUnitOfWork)).ImplementationType;

    [Fact]
    public void AddDomainModel_FlagOff_RegistersTheLegacyOnlyUnitOfWork()
    {
        var services = Register(new() { ["DomainModel:ExpandEnabled"] = "true" });

        Assert.Equal(typeof(LegacyOnlyUnitOfWork), UnitOfWork(services));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(DualWriteMetrics));
        Assert.Contains(
            services,
            d =>
                d.ServiceType == typeof(IModelInterimSiteNumbers)
                && d.ImplementationType == typeof(NoModelInterimSiteNumbers)
        );
        Assert.Contains(
            services,
            d =>
                d.ServiceType == typeof(IRegistrationNumberRules)
                && d.ImplementationType == typeof(LegacyRegistrationNumberRules)
        );
    }

    [Fact]
    public void AddDomainModel_FlagOnWithExpand_RegistersTheTransactionalUnitOfWork()
    {
        var services = Register(
            new() { ["DomainModel:ExpandEnabled"] = "true", ["DomainModel:DualWrite"] = "true" }
        );

        Assert.Equal(typeof(DualWriteUnitOfWork), UnitOfWork(services));
        Assert.Contains(services, d => d.ServiceType == typeof(DualWriteMetrics));
        Assert.Contains(
            services,
            d =>
                d.ServiceType == typeof(IApplicationGroupMigrator)
                && d.ImplementationType == typeof(ApplicationGroupMigrator)
        );
        Assert.Contains(
            services,
            d =>
                d.ServiceType == typeof(IApplicationModelSync)
                && d.ImplementationType == typeof(ApplicationModelSync)
        );
        Assert.Contains(
            services,
            d =>
                d.ServiceType == typeof(IModelInterimSiteNumbers)
                && d.ImplementationType == typeof(ModelInterimSiteNumbers)
        );
        Assert.Contains(
            services,
            d =>
                d.ServiceType == typeof(IRegistrationNumberRules)
                && d.ImplementationType == typeof(RegistrationNumberRules)
        );
    }

    [Fact]
    public void AddDomainModel_FlagOnWithoutExpand_RefusesToStart()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Register(new() { ["DomainModel:DualWrite"] = "true" })
        );

        Assert.Contains("DomainModel:DualWrite needs DomainModel:ExpandEnabled", ex.Message);
    }
}
