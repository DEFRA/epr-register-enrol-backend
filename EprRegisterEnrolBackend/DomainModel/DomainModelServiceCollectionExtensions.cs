using EprRegisterEnrolBackend.DomainModel.Diagnostics;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Seeding;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EprRegisterEnrolBackend.DomainModel;

public static class DomainModelServiceCollectionExtensions
{
    /// <summary>
    /// Registers the domain model (docs/analysis/register-enrol-domain-model-design.md) as far as the flags in
    /// <see cref="DomainModelConfig"/> allow. With every flag off, which is the default, it registers nothing that
    /// runs.
    /// </summary>
    public static IServiceCollection AddDomainModel(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var section = configuration.GetSection(DomainModelConfig.SectionName);
        var config = section.Get<DomainModelConfig>() ?? new DomainModelConfig();
        RequireExpand(config.DualWriteSeed, "DualWriteSeed", config);
        RequireExpand(config.DualWrite, "DualWrite", config);

        services.Configure<DomainModelConfig>(section);
        services.AddHostedService<DomainModelDiagnosticsService>();

        if (config.ExpandEnabled)
        {
            AddSchema(services);
        }

        AddSeedDualWrite(services, config.DualWriteSeed);
        AddDualWrite(services, config.DualWrite);

        return services;
    }

    // Phase 1, "Expand": empty versioned collections and their indexes, built at startup by
    // MongoIndexInitializerService, which also records the schema check readiness reports. Nothing reads or
    // writes them yet.
    private static void AddSchema(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<OrganisationPersistence>();
        services.AddSingleton<IDomainModelCollection>(sp =>
            sp.GetRequiredService<OrganisationPersistence>()
        );
        AddVersioned<RegistrationEntity, RegistrationPersistence>(services);
        AddVersioned<OverseasSiteEntity, OverseasSitePersistence>(services);
        AddVersioned<ApplicationEntity, ApplicationVersionPersistence>(services);
        AddVersioned<AccreditationEntity, AccreditationPersistence>(services);

        services.AddSingleton<IDomainModelSchemaStatus, DomainModelSchemaStatus>();
        services
            .AddHealthChecks()
            .AddCheck<DomainModelSchemaHealthCheck>("domain-model-schema", tags: ["ready"]);
    }

    // Phase 2, "Seed dual-write". Seed always takes an ISeedDualWriter; with the flag off it is a no-op.
    private static void AddSeedDualWrite(IServiceCollection services, bool enabled)
    {
        if (!enabled)
        {
            services.AddSingleton<ISeedDualWriter, NoOpSeedDualWriter>();
            return;
        }

        services.AddSingleton<SeedModelStores>();
        services.AddSingleton<SeedDualWriteMetrics>();
        services.AddSingleton<ISeedDualWriter, SeedDualWriter>();
    }

    // Phase 3, "Full dual-write". Every write path takes an IDualWriteUnitOfWork; with the flag off it runs
    // the legacy write alone, exactly as before.
    private static void AddDualWrite(IServiceCollection services, bool enabled)
    {
        if (!enabled)
        {
            services.AddSingleton<IDualWriteUnitOfWork, LegacyOnlyUnitOfWork>();
            return;
        }

        services.AddSingleton<DualWriteMetrics>();
        services.AddSingleton<IDualWriteUnitOfWork, DualWriteUnitOfWork>();
    }

    private static void RequireExpand(bool flag, string name, DomainModelConfig config)
    {
        if (flag && !config.ExpandEnabled)
        {
            throw new InvalidOperationException(
                $"DomainModel:{name} needs DomainModel:ExpandEnabled: without it there are no collections to write to."
            );
        }
    }

    private static void AddVersioned<TEntity, TPersistence>(IServiceCollection services)
        where TEntity : VersionedEntityBase
        where TPersistence : class, IVersionedPersistence<TEntity>
    {
        services.AddSingleton<IVersionedPersistence<TEntity>, TPersistence>();
        services.AddSingleton<IDomainModelCollection>(sp =>
            sp.GetRequiredService<IVersionedPersistence<TEntity>>()
        );
    }
}
