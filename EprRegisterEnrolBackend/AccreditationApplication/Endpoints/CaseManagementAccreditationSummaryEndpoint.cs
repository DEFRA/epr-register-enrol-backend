using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.DomainModel.ReadModel;

namespace EprRegisterEnrolBackend.AccreditationApplication.Endpoints;

/// <summary>
/// RA-769: the accreditation as it stands today, for the Accreditation summary tab of an In-Year Change case in
/// the Case Management service. Read-only, on the CaseManagement scheme (the caller is management-be, which needs no
/// new credential), addressed by operator organisation id and application id as every other route here is.
///
/// The body is its own allow-listed record, never the persistence model, so a field added to the model is never
/// served by accident. An application that does not exist, or is not <see cref="ApplicationStatus.Approved"/>, is
/// a 404: there is no accreditation to show.
/// </summary>
internal static class CaseManagementAccreditationSummaryEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group
            .MapGet(
                "case-management/{organisationId}/{applicationId}/accreditation-summary",
                Get
            )
            .WithName("GetCaseManagementAccreditationSummary")
            .RequireCaseManagementCaller();

    private static async Task<IResult> Get(
        string organisationId,
        string applicationId,
        IApplicationReader reader,
        CancellationToken cancellationToken
    )
    {
        var application = await reader.GetByIdAsync(organisationId, applicationId, cancellationToken);
        if (application is null || application.ApplicationStatus != ApplicationStatus.Approved)
        {
            return Results.NotFound();
        }

        return Results.Ok(CaseManagementAccreditationSummary.From(application));
    }
}
