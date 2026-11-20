using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Utils;
using Microsoft.AspNetCore.Authorization;

namespace EprRegisterEnrolBackend.Registrations;

/// <summary>
/// The current status of a registration, as the operator journey's material dashboard pages show it. RA-697: with
/// the organisation name, material, exporter flag and site address, from which a registration-only operator's
/// pages are built.
/// </summary>
public sealed record RegistrationStatusResponse(
    string RegistrationId,
    string? RegistrationNumber,
    RegistrationStatus Status,
    string? OrganisationName,
    MaterialType MaterialType,
    GlassRecyclingProcess? GlassRecyclingProcess,
    bool IsExporter,
    string? SiteAddress,
    string? ReExOrganisationId = null
);

/// <summary>
/// RA-425: the registration status read by the frontend's material dashboard pages, from the domain model's
/// current registration version. Kept out of the accreditation-application endpoints, which RA-644 is rewriting.
/// </summary>
public static class RegistrationStatusEndpoints
{
    public static void UseRegistrationStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("api/v1/registrations/{registrationId}/status", GetStatus)
            .RequireAuthorization(FrontendOnly);
        app.MapGet("api/v1/registrations/by-number/{registrationNumber}/status", GetStatusByNumber)
            .RequireAuthorization(FrontendOnly);
    }

    private static void FrontendOnly(AuthorizationPolicyBuilder policy) =>
        policy
            .AddAuthenticationSchemes(FrontendAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser();

    // registrationId is the ReEx registration id. RA-697: organisationId is the ReEx organisation id, and required.
    // ReEx names it, and must say it holds the registration: a registration-only page has no accreditation to tie
    // the two together.
    private static async Task<IResult> GetStatus(
        string registrationId,
        string? organisationId,
        RegistrationVersionStore store,
        IReExApiAdapter reEx,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(organisationId))
            return Results.BadRequest("organisationId is required.");

        var current = await store.FindCurrentByReExRegistrationIdAsync(
            registrationId,
            cancellationToken
        );
        return await StatusAsync(current, organisationId, reEx, cancellationToken);
    }

    // RA-739: the frontend's manage page URLs carry the registration number, not the ReEx registration id. Only the
    // current version's number matches, so a superseded number is a 404. ReEx is then asked with that version's
    // ReEx registration id, so a number another organisation holds is a 404 too.
    //
    // The URL's organisationId is the organisation's number, never ReEx's id. It is 404 unless it is a number, and
    // unless it is the number of the organisation ReEx finds holding the registration; the same when ReEx records no
    // number or id for that organisation, as there is then nothing to resolve. The response carries ReEx's id for
    // the organisation, for the frontend's other calls. Unlike the status by registration id, this fails when ReEx
    // cannot answer: without the organisation there is neither the id nor the check that the pair belongs together.
    private static async Task<IResult> GetStatusByNumber(
        string registrationNumber,
        string? organisationId,
        RegistrationVersionStore store,
        IReExApiAdapter reEx,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(organisationId))
            return Results.BadRequest("organisationId is required.");
        if (
            !int.TryParse(
                organisationId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var organisationNumber
            )
        )
            return Results.NotFound();

        var current = await store.FindCurrentByRegistrationNumberAsync(
            registrationNumber,
            cancellationToken
        );
        if (current?.ReExRegistrationId is not { } registrationId)
            return Results.NotFound();

        var holder = await reEx.GetRegistrationHolderAsync(organisationId, registrationId, cancellationToken);
        if (holder.IsNotFound)
            return Results.NotFound();
        if (!holder.IsSuccess)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var organisation = holder.Value!;
        if (organisation.OrganisationNumber != organisationNumber || organisation.ReExOrganisationId is null)
        {
            loggerFactory
                .CreateLogger(nameof(RegistrationStatusEndpoints))
                .LogWarning(
                    "Registration {RegistrationNumber} is not under organisation number {OrganisationNumber}: ReEx "
                        + "records number {ReExOrganisationNumber} and id {ReExOrganisationIdPresent} for its holder",
                    registrationNumber,
                    organisationNumber,
                    organisation.OrganisationNumber,
                    organisation.ReExOrganisationId is not null
                );
            return Results.NotFound();
        }

        return Results.Ok(Response(current, registrationId, organisation.Name, organisation.ReExOrganisationId));
    }

    // A version without a ReEx registration id cannot be checked against ReEx, so it has no status to show.
    private static async Task<IResult> StatusAsync(
        RegistrationEntity? current,
        string organisationId,
        IReExApiAdapter reEx,
        CancellationToken cancellationToken
    )
    {
        if (current?.ReExRegistrationId is not { } registrationId)
            return Results.NotFound();

        var holder = await reEx.GetRegistrationHolderNameAsync(organisationId, registrationId, cancellationToken);
        if (holder.IsNotFound)
            return Results.NotFound();
        // Fails open, deliberately: any other failure leaves the name out rather than failing the status, which
        // every manage page needs, accredited ones included, so failing closed would take them all down in a ReEx
        // blip. The frontend shows no registration-only page without the name.
        var organisationName = holder.IsSuccess ? holder.Value : null;

        return Results.Ok(Response(current, registrationId, organisationName, reExOrganisationId: null));
    }

    private static RegistrationStatusResponse Response(
        RegistrationEntity current,
        string registrationId,
        string? organisationName,
        string? reExOrganisationId
    ) =>
        new(
            registrationId,
            current.RegistrationNumber,
            current.Status,
            organisationName,
            current.Material,
            current.GlassRecyclingProcess,
            current.WasteProcessingType == WasteProcessingKind.Exporter,
            SiteAddress(current.Site?.Address),
            reExOrganisationId
        );

    // As an accreditation application's site address is (Line1, town, postcode). An address with nothing in it
    // is no address, so null rather than "".
    private static string? SiteAddress(PostalAddress? address)
    {
        var joined = address is null
            ? ""
            : AddressFormatting.Join(address.Line1, address.TownOrCity, address.Postcode);
        return joined.Length == 0 ? null : joined;
    }
}
