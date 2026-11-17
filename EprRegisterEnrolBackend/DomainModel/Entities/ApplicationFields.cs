using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// RA-706: the stored names of the <see cref="ApplicationEntity"/> fields that typed filters can't reach (a computed
/// field, or one inside the polymorphic <see cref="ApplicationEntity.Details"/>), written once so a query, an index and
/// the mapping can't drift apart.
/// </summary>
public static class ApplicationFields
{
    public const string ApplicationType = "applicationType";
    public const string ReservedAccreditationNumber = "details.reservedAccreditationNumber";
    public const string AccreditationId = "details.accreditationId";

    /// <summary>Accreditation applications only (the other types share the collection).</summary>
    public static FilterDefinition<ApplicationEntity> Accreditations =>
        Builders<ApplicationEntity>.Filter.Eq(
            ApplicationType,
            nameof(Entities.ApplicationType.Accreditation)
        );
}
