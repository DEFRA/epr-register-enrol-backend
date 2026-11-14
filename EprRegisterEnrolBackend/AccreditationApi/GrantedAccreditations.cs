using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationApi;

/// <summary>
/// The granted accreditations the service-client API serves: found in legacy as today,
/// their content served by the configured source - legacy, or the model with DomainModel:ReadFromModel on; RA-705
/// </summary>
public sealed class GrantedAccreditations(
    IAccreditationApplicationPersistence persistence,
    IApplicationReader reader,
    IOptions<ServiceClientsConfig> config
)
{
    public async Task<AccreditationApplicationModel?> FindAsync(
        int organisationNumber,
        string registrationNumber,
        int year,
        CancellationToken cancellationToken
    )
    {
        var application = await persistence.GetGrantedAccreditationAsync(
            organisationNumber,
            registrationNumber,
            year,
            config.Value.FirstAccreditationYear
        );

        return application is null
            ? null
            : await reader.ServeFromSourceAsync(application, cancellationToken);
    }

    public async Task<(IReadOnlyList<AccreditationApplicationModel> Items, long Total)> SearchAsync(
        GrantedAccreditationFilter filter,
        int skip,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var (items, total) = await persistence.SearchGrantedAccreditationsAsync(
            filter,
            config.Value.FirstAccreditationYear,
            skip,
            limit
        );

        return (await reader.ServeManyFromSourceAsync([.. items], cancellationToken), total);
    }
}
