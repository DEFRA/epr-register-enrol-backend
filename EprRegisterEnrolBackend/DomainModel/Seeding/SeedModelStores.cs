using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;

namespace EprRegisterEnrolBackend.DomainModel.Seeding;

/// <summary>The domain model collections a seed writes to.</summary>
public sealed record SeedModelStores(
    OrganisationPersistence Organisations,
    IVersionedPersistence<RegistrationEntity> Registrations,
    IVersionedPersistence<OverseasSiteEntity> OverseasSites
);
