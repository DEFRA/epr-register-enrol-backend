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
    // RA-702: the database stops a query that runs past this, and the API answers 503.
    private TimeSpan? QueryTimeout =>
        config.Value.QueryTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(config.Value.QueryTimeoutSeconds)
            : null;

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
            config.Value.FirstAccreditationYear,
            QueryTimeout
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
            limit,
            QueryTimeout
        );

        return (await reader.ServeManyFromSourceAsync([.. items], cancellationToken), total);
    }
}
