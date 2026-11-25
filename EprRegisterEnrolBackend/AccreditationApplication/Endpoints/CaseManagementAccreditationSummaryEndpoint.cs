using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.DomainModel.ReadModel;

namespace EprRegisterEnrolBackend.AccreditationApplication.Endpoints;

/// <summary>
/// RA-769: the accreditation as it stands today, for the Accreditation summary tab of an In-Year Change case in
/// the Case Management service. Read-only, on the CaseManagement scheme (the caller is management-be, which needs no
/// new credential), addressed by operator organisation id and application id, as the operator-facing routes of this
/// group are (the case management query and status routes are addressed by work item id instead).
///
/// The body is its own allow-listed record, never the persistence model, so a field added to the model is never
/// served by accident. An application that does not exist, or is not <see cref="ApplicationStatus.Approved"/>, is
/// a 404: there is no accreditation to show. The 404 is logged as a warning, with the organisation and application id,
/// so a wrong id sent by Case Management is visible rather than a bare 404.
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
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var logger = loggerFactory.CreateLogger(nameof(CaseManagementAccreditationSummaryEndpoint));
        var application = await reader.GetByIdAsync(organisationId, applicationId, cancellationToken);
        if (application is null)
        {
            logger.LogWarning(
                "Case management accreditation summary: no application found for organisationId={OrganisationId} applicationId={ApplicationId}",
                organisationId,
                applicationId
            );
            return Results.NotFound();
        }

        if (application.ApplicationStatus != ApplicationStatus.Approved)
        {
            logger.LogWarning(
                "Case management accreditation summary: application is not Approved, organisationId={OrganisationId} applicationId={ApplicationId} status={ApplicationStatus}",
                organisationId,
                applicationId,
                application.ApplicationStatus
            );
            return Results.NotFound();
        }

        return Results.Ok(CaseManagementAccreditationSummary.From(application));
    }
}
