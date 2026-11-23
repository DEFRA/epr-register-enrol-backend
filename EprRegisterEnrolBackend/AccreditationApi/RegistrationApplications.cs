using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationApi;

/// <summary>
/// The applications the service-client API serves as a registration: every application for the
/// organisation's registration, whatever its status or year, found and served as
/// <see cref="GrantedAccreditations"/> finds and serves a granted one.
/// </summary>
public sealed class RegistrationApplications(
    IAccreditationApplicationPersistence persistence,
    IApplicationReader reader,
    IOptions<ServiceClientsConfig> config
)
{
    // RA-702: the database stops a query that runs past this, and the API answers 503.
    private TimeSpan? QueryTimeout => config.Value.QueryTimeout;

    public async Task<IReadOnlyList<AccreditationApplicationModel>> FindAsync(
        int organisationNumber,
        string registrationNumber,
        CancellationToken cancellationToken
    )
    {
        var applications = await persistence.GetByOrgIdAndRegistrationNumberAsync(
            organisationNumber,
            registrationNumber,
            QueryTimeout
        );

        return await reader.ServeManyFromSourceAsync(applications, cancellationToken);
    }
}
