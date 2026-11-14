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
/// <c>src/server/common/helpers/paymentDetails.js</c>. See "How payment references are built" in
/// docs/analysis/register-enrol-application-types.md.
/// </summary>
public static class PaymentReferenceBuilder
{
    // The range management-be accepts for operatorOrgNumber. A value outside it is treated as no
    // organisation number at all.
    public const int MinOrgNumber = 1;
    public const int MaxOrgNumber = 999_999;

    public static bool IsValidOrgNumber(int orgNumber) =>
        orgNumber is >= MinOrgNumber and <= MaxOrgNumber;

    public static string Build(Nation nation, int orgNumber, bool isExporter)
    {
        if (!IsValidOrgNumber(orgNumber))
            throw new ArgumentOutOfRangeException(
                nameof(orgNumber),
                orgNumber,
                $"An organisation number must be from {MinOrgNumber} to {MaxOrgNumber}."
            );
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

    /// <summary>Null when the organisation number is missing or out of range, or the nation is missing.</summary>
    public static string? TryBuild(AccreditationApplicationModel application) =>
        application is { OrgId: { } orgNumber, Nation: { } nation } && IsValidOrgNumber(orgNumber)
            ? Build(nation, orgNumber, application.IsExporter)
            : null;

    /// <summary>
    /// The reference a single-application read returns, built from the application's current
    /// OrgId and Nation. A stored reference that differs (e.g. one ending "/null", captured from
    /// the frontend before RA-000) is logged at Error so it can be migrated, and the built one is
    /// returned in its place; the stored one is not changed.
    /// </summary>
    public static string? BuildForRead(AccreditationApplicationModel application, ILogger logger)
    {
        var built = TryBuild(application);
        if (application.PaymentReference is { } stored && stored != built)
            logger.LogError(
                "Stored payment reference {StoredPaymentReference} differs from the one built from "
                    + "the organisation number and nation ({BuiltPaymentReference}) for "
                    + "applicationId={ApplicationId} organisationId={OrganisationId}; the built one is returned",
                stored,
                built ?? "(none)",
                application.ApplicationId,
                application.OrganisationId
            );
        return built;
    }
}

/// <summary>
/// RA-000: the one place the organisation number and nation an application is missing are looked
/// up in ReEx. A stored value is used as it is and never replaced; a value is looked up only when
/// none is stored, and set (in memory - the caller saves it) only when ReEx gives a usable one.
/// </summary>
public static class OrganisationDetailsLookup
{
    /// <param name="Changed">True when a looked-up value was set on the application.</param>
    /// <param name="OrgNumberProblem">Why there is no usable organisation number, or null.</param>
    /// <param name="NationProblem">Why there is no nation, or null.</param>
    public sealed record Outcome(bool Changed, string? OrgNumberProblem, string? NationProblem);

    public static async Task<Outcome> FillMissingAsync(
        AccreditationApplicationModel application,
        IReExApiAdapter reEx,
        CancellationToken cancellationToken
    )
    {
        var changed = false;
        string? orgNumberProblem = null;
        string? nationProblem = null;

        if (application.OrgId is { } stored)
        {
            if (!PaymentReferenceBuilder.IsValidOrgNumber(stored))
                orgNumberProblem = $"stored value {stored} is outside {Range}";
        }
        else
        {
            var orgNumber = await reEx.GetOrganisationNumberAsync(
                application.OrganisationId,
                cancellationToken
            );
            if (orgNumber is { IsSuccess: true, Value: { } found })
            {
                if (PaymentReferenceBuilder.IsValidOrgNumber(found))
                {
                    application.OrgId = found;
                    changed = true;
                }
                else
                    orgNumberProblem = $"ReEx returned {found}, outside {Range}";
            }
            else
                orgNumberProblem = Describe(orgNumber);
        }

        if (application.Nation is null)
        {
            if (application.RegistrationId is null)
                nationProblem = "not looked up: the application has no registrationId";
            else
            {
                var nation = await reEx.GetNationAsync(
                    application.OrganisationId,
                    application.RegistrationId,
                    cancellationToken
                );
                if (nation is { IsSuccess: true })
                {
                    application.Nation = nation.Value;
                    changed = true;
                }
                else
                    nationProblem = Describe(nation);
            }
        }

        return new Outcome(changed, orgNumberProblem, nationProblem);
    }

    private static string Range =>
        $"{PaymentReferenceBuilder.MinOrgNumber}-{PaymentReferenceBuilder.MaxOrgNumber}";

    private static string Describe<T>(ReExResult<T>? result) =>
        result switch
        {
            null => "ReEx returned no result",
            { IsSuccess: true } => $"ReEx status={result.StatusCode} returned no value",
            _ => $"ReEx status={result.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} kind={result.Error?.Kind}",
        };
}

/// <summary>The inputs a payment reference needs, all present, and the reference built from them.</summary>
public sealed record ResolvedPaymentReference(int OrgId, Nation Nation, string PaymentReference);

/// <summary>
/// RA-000: gets everything a payment reference needs before anything is sent to Case Management,
/// for an application's submit and an in-year change request's submit. Missing values are filled
/// by <see cref="OrganisationDetailsLookup"/>. If the organisation number (or one in range) or the
/// nation is still missing, the caller gets a 503 ProblemDetails to return as-is and an Error log
/// is written. No value is made up: there is no fallback organisation number, and a missing stored
/// nation is not defaulted.
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
        var outcome = await OrganisationDetailsLookup.FillMissingAsync(
            application,
            reEx,
            cancellationToken
        );
        if (outcome.OrgNumberProblem is { } orgNumberProblem)
            return (
                null,
                Unavailable(
                    application,
                    operation,
                    OrgNumberUnavailableTitle,
                    "organisation number",
                    orgNumberProblem,
                    logger
                )
            );
        if (outcome.NationProblem is { } nationProblem)
            return (
                null,
                Unavailable(
                    application,
                    operation,
                    NationUnavailableTitle,
                    "nation",
                    nationProblem,
                    logger
                )
            );

        var orgId = application.OrgId!.Value;
        var nation = application.Nation!.Value;
        return (
            new ResolvedPaymentReference(
                orgId,
                nation,
                PaymentReferenceBuilder.Build(nation, orgId, application.IsExporter)
            ),
            null
        );
    }

    private static IResult Unavailable(
        AccreditationApplicationModel application,
        string operation,
        string title,
        string missing,
        string problem,
        ILogger logger
    )
    {
        logger.LogError(
            "{Operation} blocked: no {Missing} for applicationId={ApplicationId} "
                + "organisationId={OrganisationId} ({Problem}); nothing was sent to Case Management",
            operation,
            missing,
            application.ApplicationId,
            application.OrganisationId,
            problem
        );
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: title,
            detail: $"The {missing} needed for the payment reference could not be found, so nothing was submitted. Try again later."
        );
    }
}
