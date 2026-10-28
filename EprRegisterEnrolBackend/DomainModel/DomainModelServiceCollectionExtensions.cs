using EprRegisterEnrolBackend.DomainModel.Diagnostics;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
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
        services.Configure<DomainModelConfig>(section);
        services.AddHostedService<DomainModelDiagnosticsService>();

        if (section.Get<DomainModelConfig>()?.ExpandEnabled == true)
        {
            AddSchema(services);
        }

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
