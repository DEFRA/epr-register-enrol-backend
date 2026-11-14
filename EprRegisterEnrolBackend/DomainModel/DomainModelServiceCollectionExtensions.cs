using EprRegisterEnrolBackend.DomainModel.Diagnostics;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using EprRegisterEnrolBackend.DomainModel.Reconciliation;
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
        RequireDualWrite(config.MigrateEnabled, "MigrateEnabled", config);
        RequireDualWrite(config.ReconcileEnabled, "ReconcileEnabled", config);

        services.Configure<DomainModelConfig>(section);
        services.AddHostedService<DomainModelDiagnosticsService>();

        if (config.ExpandEnabled)
        {
            AddSchema(services);
        }

        AddSeedDualWrite(services, config.DualWriteSeed);
        AddDualWrite(services, config.DualWrite);
        AddMigration(services, config.MigrateEnabled);
        AddReconciliation(services, config.ReconcileEnabled);
        AddReadFromModel(services, config.ReadFromModel);

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
        services.AddSingleton<MigrationConflictPersistence>();
        services.AddSingleton<IDomainModelCollection>(sp =>
            sp.GetRequiredService<MigrationConflictPersistence>()
        );

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
            services.AddSingleton<IModelInterimSiteNumbers, NoModelInterimSiteNumbers>();
            services.AddSingleton<IRegistrationNumberRules, LegacyRegistrationNumberRules>();
            return;
        }

        services.AddSingleton<DualWriteMetrics>();
        services.AddSingleton<ApplicationGroupStores>();
        services.AddSingleton<IApplicationGroupMigrator, ApplicationGroupMigrator>();
        services.AddSingleton<IApplicationModelSync, ApplicationModelSync>();
        services.AddSingleton<IModelInterimSiteNumbers, ModelInterimSiteNumbers>();
        services.AddSingleton<IRegistrationNumberRules, RegistrationNumberRules>();
        services.AddSingleton<IDualWriteUnitOfWork, DualWriteUnitOfWork>();
    }

    // Phase 4: the background migration, and the readiness signal that shows a group it could not migrate.
    private static void AddMigration(IServiceCollection services, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        services.AddSingleton<DomainModelMigrationPersistence>();
        services.AddSingleton<IDomainModelMigrationStatus, DomainModelMigrationStatus>();
        services
            .AddHealthChecks()
            .AddCheck<DomainModelMigrationHealthCheck>("domain-model-migration", tags: ["ready"]);
        services.AddHostedService<DomainModelMigrationService>();
    }

    // Phase 4: the projection the reads will switch to, and the report comparing it with the legacy documents.
    private static void AddReconciliation(IServiceCollection services, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        services.TryAddSingleton<ApplicationReadModel>();
        services.AddSingleton<ApplicationReconciler>();
        services.AddHostedService<DomainModelReconciliationService>();
    }

    // Phase 5, "Switch reads": switches the IApplicationReader implementation based on the configuration.
    private static void AddReadFromModel(IServiceCollection services, bool enabled)
    {
        if (!enabled)
        {
            services.AddSingleton<IApplicationReader, LegacyApplicationReader>();
            return;
        }

        services.TryAddSingleton<ApplicationGroupStores>();
        services.TryAddSingleton<ApplicationReadModel>();
        services.AddSingleton<IApplicationReader, ModelApplicationReader>();
    }

    private static void RequireDualWrite(bool flag, string name, DomainModelConfig config)
    {
        if (flag && !config.DualWrite)
        {
            throw new InvalidOperationException(
                $"DomainModel:{name} needs DomainModel:DualWrite: without it a migrated application goes stale."
            );
        }
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
