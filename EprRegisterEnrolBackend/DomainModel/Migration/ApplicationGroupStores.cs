using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;

namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>The domain model collections the migration writes to.</summary>
public sealed record ApplicationGroupStores(
    OrganisationPersistence Organisations,
    IVersionedPersistence<RegistrationEntity> Registrations,
    IVersionedPersistence<OverseasSiteEntity> OverseasSites,
    IVersionedPersistence<ApplicationEntity> Applications,
    IVersionedPersistence<AccreditationEntity> Accreditations,
    MigrationConflictPersistence Conflicts
);
