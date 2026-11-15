using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Cut;

/// <summary>
/// RA-706 decision 6 - with LegacyWrites off this replaces the legacy persistence; reads project from the model;
/// the version is the model's, the concurrency token after the cut.
/// </summary>
public sealed class ModelBackedApplicationPersistence(
    IMongoDbClientFactory clientFactory,
    ApplicationReadModel readModel
) : IAccreditationApplicationPersistence
{
    private async Task<AccreditationApplicationModel?> ProjectAsync(
        ObjectId id,
        IClientSessionHandle session,
        CancellationToken ct
    )
    {
        var sources = await readModel.LoadAsync(id, session, ct);
        if (sources is null)
            return null;

        var legacy = ApplicationProjection.ToLegacy(sources);

        legacy.Version = sources.Application.Version;
        legacy.CreatedAt = sources.Application.CreatedAt ?? sources.Application.ValidFrom;
        legacy.UpdatedAt = sources.Application.ValidFrom;

        legacy.RegistrationEntityId = sources.Application.RegistrationId;
        legacy.AccreditationEntityId = (
            (AccreditationDetails)sources.Application.Details
        ).AccreditationId;

        return legacy;
    }

    public Task<AccreditationApplicationModel?> GetByObjectIdAsync(
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    ) => ProjectAsync(applicationId, session, cancellationToken);

    public async Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId,
        IClientSessionHandle? session = null)
    {
        if (!ObjectId.TryParse(applicationId, out var id)) return null;

        using var own = session is null ? await clientFactory.GetClient().StartSessionAsync() : null;
        var projected = await ProjectAsync(id, session ?? own!, default);

        return projected?.OrganisationId == organisationId ? projected : null;
    }

    public Task<AccreditationApplicationModel?> CreateAsync(
        AccreditationApplicationModel application
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task UpsertAsync(AccreditationApplicationModel application) =>
        throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<long> DeleteManyAsync(IEnumerable<ObjectId> ids) =>
        throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<IEnumerable<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<AccreditationApplicationModel?> GetLiveByRegistrationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year,
        IClientSessionHandle? session = null
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<AccreditationApplicationModel?> GetLatestPreviousAccreditationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        GlassRecyclingProcess? glassRecyclingProcess,
        int beforeYear
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<
        IReadOnlyList<AccreditationReferenceHolder>
    > GetOtherAccreditationReferenceHoldersAsync(
        string accreditationReference,
        ObjectId applicationId
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<AccreditationApplicationModel?> GetGrantedAccreditationAsync(
        int orgId,
        string registrationNumber,
        int year,
        int firstYear
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<(
        IReadOnlyList<AccreditationApplicationModel> Items,
        long Total
    )> SearchGrantedAccreditationsAsync(
        GrantedAccreditationFilter filter,
        int firstYear,
        int skip,
        int limit
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<UnservableApprovedCounts> CountUnservableApprovedAsync(
        int firstYear,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<IReadOnlyList<string>> GetOrsIdsByRegistrationAsync(
        string registrationId,
        IClientSessionHandle? session = null
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<IReadOnlyList<string>> GetInterimSiteNumbersByRegistrationAsync(
        string registrationId,
        IClientSessionHandle? session = null
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<AccreditationApplicationModel?> UpdateIfInterimSiteNumberAbsentAsync(
        AccreditationApplicationModel application,
        string siteNumber,
        IClientSessionHandle? session = null
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<AccreditationApplicationModel?> GetNewestForOrganisationAsync(
        string organisationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<IReadOnlyList<AccreditationApplicationModel>> GetRegistrationGroupAsync(
        string organisationId,
        string registrationId,
        int limit,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<AccreditationApplicationModel?> GetByCaseManagementWorkItemIdAsync(
        Guid workItemId,
        IClientSessionHandle? session = null
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<IReadOnlyList<AccreditationApplicationModel>> GetByAccreditationReferenceAsync(
        string accreditationReference,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<IReadOnlyList<AccreditationApplicationModel>> GetByOrgIdAndYearAsync(
        int orgId,
        int year,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<AccreditationApplicationModel?> UpdateAsync(
        AccreditationApplicationModel application,
        IClientSessionHandle? session = null
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<AccreditationApplicationModel?> UpdateIfOrsIdAbsentAsync(
        AccreditationApplicationModel application,
        string orsId,
        IClientSessionHandle? session = null
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task<AccreditationApplicationModel?> UpdateFieldsAsync(
        ObjectId id,
        FilterDefinition<AccreditationApplicationModel>? guardFilter,
        UpdateDefinition<AccreditationApplicationModel> update,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("RA-706: not yet served from the model");

    public Task StampDomainModelForeignKeysAsync(
        ObjectId applicationId,
        ObjectId registrationEntityId,
        IReadOnlyDictionary<string, ObjectId> overseasSiteEntityIds,
        ObjectId? accreditationEntityId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("RA-706: not yet served from the model");
}
