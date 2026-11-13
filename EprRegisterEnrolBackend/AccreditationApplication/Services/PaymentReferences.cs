using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.ReEx;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// RA-000: the operator's nation-specific bank payment reference. Built here, by the backend,
/// from the organisation number (ReEx's numeric <c>orgId</c>) and the nation. It is no longer
/// accepted from the frontend. The formats are the ones the operator is told to quote, ported
/// exactly from <c>buildPaymentReference</c> in epr-register-enrol-frontend's
/// <c>src/server/common/helpers/paymentDetails.js</c>.
/// </summary>
public static class PaymentReferenceBuilder
{
    public static string Build(Nation nation, int orgNumber, bool isExporter)
    {
        var number = orgNumber.ToString(CultureInfo.InvariantCulture);
        return nation switch
        {
            Nation.NorthernIreland => $"NI/PR/REEX/{number}",
            Nation.Wales => $"PREX/{number}",
            Nation.Scotland => $"E800 81581/{number}",
            Nation.England => isExporter ? $"PR/PK/EXP/{number}" : $"PR/PK/REP/{number}",
            _ => throw new ArgumentOutOfRangeException(
                nameof(nation),
                nation,
                "No payment reference format is defined for this nation."
            ),
        };
    }

    /// <summary>Null when the organisation number or the nation is missing.</summary>
    public static string? TryBuild(AccreditationApplicationModel application) =>
        application is { OrgId: { } orgNumber, Nation: { } nation }
            ? Build(nation, orgNumber, application.IsExporter)
            : null;

    /// <summary>
    /// RA-000 (C4): the reference a read returns, built from the application's current OrgId and
    /// Nation. A stored reference that differs (e.g. one ending "/null", captured from the
    /// frontend before this change) is logged at Error so it can be migrated, and the built one
    /// is returned in its place.
    /// </summary>
    public static string? BuildForRead(AccreditationApplicationModel application, ILogger logger)
    {
        var built = TryBuild(application);
        LogIfStoredDiffers(application, built, "read", logger);
        return built;
    }

    public static void LogIfStoredDiffers(
        AccreditationApplicationModel application,
        string? built,
        string operation,
        ILogger logger
    )
    {
        if (application.PaymentReference is { } stored && stored != built)
            logger.LogError(
                "Stored payment reference {StoredPaymentReference} differs from the one built from "
                    + "the organisation number and nation ({BuiltPaymentReference}) on {Operation} "
                    + "for applicationId={ApplicationId} organisationId={OrganisationId}; the built one is used",
                stored,
                built ?? "(none)",
                operation,
                application.ApplicationId,
                application.OrganisationId
            );
    }
}

/// <summary>The inputs a payment reference needs, all present, and the reference built from them.</summary>
public sealed record ResolvedPaymentReference(int OrgId, Nation Nation, string PaymentReference);

/// <summary>
/// RA-000 (C1/C2): gets everything a payment reference needs before anything is sent to Case
/// Management. The stored OrgId and Nation are used as they are; each is looked up in ReEx only
/// when it is not stored, and a stored value is never replaced. If either is still missing, the
/// caller gets a 503 ProblemDetails to return as-is and an Error log is written. No value is made
/// up: there is no default nation and no fallback organisation number.
/// </summary>
public static class PaymentReferenceResolver
{
    public const string OrgNumberUnavailableTitle = "Organisation number unavailable";
    public const string NationUnavailableTitle = "Nation unavailable";

    /// <summary>
    /// Sets any looked-up OrgId/Nation on <paramref name="application"/> (in memory only - the
    /// caller saves them with its own write) and returns the reference built from them.
    /// </summary>
    public static async Task<(ResolvedPaymentReference? Resolved, IResult? Error)> ResolveAsync(
        AccreditationApplicationModel application,
        string operation,
        IReExApiAdapter reEx,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        if (application.OrgId is null)
        {
            var orgNumber = await reEx.GetOrganisationNumberAsync(
                application.OrganisationId,
                cancellationToken
            );
            if (orgNumber is { IsSuccess: true, Value: { } found })
                application.OrgId = found;
            else
                return (
                    null,
                    Unavailable(
                        application,
                        operation,
                        OrgNumberUnavailableTitle,
                        "organisation number",
                        Describe(orgNumber),
                        logger
                    )
                );
        }

        if (application.Nation is null)
        {
            if (application.RegistrationId is null)
                return (
                    null,
                    Unavailable(
                        application,
                        operation,
                        NationUnavailableTitle,
                        "nation",
                        "not looked up: the application has no registrationId",
                        logger
                    )
                );

            var nation = await reEx.GetNationAsync(
                application.OrganisationId,
                application.RegistrationId,
                cancellationToken
            );
            if (nation is { IsSuccess: true })
                application.Nation = nation.Value;
            else
                return (
                    null,
                    Unavailable(
                        application,
                        operation,
                        NationUnavailableTitle,
                        "nation",
                        Describe(nation),
                        logger
                    )
                );
        }

        var orgId = application.OrgId.Value;
        var resolvedNation = application.Nation.Value;
        return (
            new ResolvedPaymentReference(
                orgId,
                resolvedNation,
                PaymentReferenceBuilder.Build(resolvedNation, orgId, application.IsExporter)
            ),
            null
        );
    }

    private static string Describe<T>(ReExResult<T>? result) =>
        result switch
        {
            null => "ReEx returned no result",
            { IsSuccess: true } => $"ReEx status={result.StatusCode} returned no value",
            _ => $"ReEx status={result.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} kind={result.Error?.Kind}",
        };

    private static IResult Unavailable(
        AccreditationApplicationModel application,
        string operation,
        string title,
        string missing,
        string reExOutcome,
        ILogger logger
    )
    {
        logger.LogError(
            "{Operation} blocked: no {Missing} for applicationId={ApplicationId} "
                + "organisationId={OrganisationId} ({ReExOutcome}); nothing was sent to Case Management",
            operation,
            missing,
            application.ApplicationId,
            application.OrganisationId,
            reExOutcome
        );
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: title,
            detail: $"The {missing} needed for the payment reference could not be found, so nothing was submitted. Try again later."
        );
    }
}
