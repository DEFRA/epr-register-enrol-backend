using System.Security.Claims;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Validators;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.Utils;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationApplication.Endpoints;

public static class AccreditationApplicationEndpoints
{
    // Every route here is called only by epr-register-enrol-frontend, except the two
    // case-management/* routes (ManagementBe, its own CaseManagement scheme below) and
    // files/upload-completed (the CDP Uploader webhook callback — a third caller with no
    // shared-secret scheme established in this codebase; applying Frontend auth to it
    // would break real uploads, since CDP has no way to send that secret).
    private static void FrontendOnly(IEndpointConventionBuilder endpoint) =>
        endpoint.RequireAuthorization(policy =>
            policy
                .AddAuthenticationSchemes(FrontendAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
        );

    public static void UseAccreditationApplicationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/v1/accreditation-applications");

        FrontendOnly(group.MapPost("{organisationId}/{registrationId}/{materialType}/seed", Seed));
        FrontendOnly(group.MapGet("{organisationId}", GetList));
        FrontendOnly(group.MapGet("{organisationId}/{applicationId}", GetById));
        FrontendOnly(group.MapPatch("{organisationId}/{applicationId}/prns", PatchPrns));
        FrontendOnly(group.MapPatch("{organisationId}/{applicationId}/tonnage", PatchTonnage));
        FrontendOnly(
            group.MapPatch("{organisationId}/{applicationId}/business-plan", PatchBusinessPlan)
        );
        FrontendOnly(
            group.MapPatch("{organisationId}/{applicationId}/sampling-plan", PatchSamplingPlan)
        );
        FrontendOnly(
            group.MapPatch("{organisationId}/{applicationId}/overseas-sites", PatchOverseasSites)
        );
        FrontendOnly(
            group.MapPost("{organisationId}/{applicationId}/overseas-sites", AddOverseasSite)
        );
        // RA-470: in-place "Change" edit of an already-selected overseas site - sibling to
        // PromoteOverseasSite (below) but never sets Selected/RegisteredNowAccredited, since the
        // site is already selected/accredited and this isn't a promotion from a registered site.
        FrontendOnly(
            group.MapPatch(
                "{organisationId}/{applicationId}/overseas-sites/{siteId}",
                UpdateOverseasSite
            )
        );
        FrontendOnly(
            group.MapPost(
                "{organisationId}/{applicationId}/overseas-sites/{siteId}/promote",
                PromoteOverseasSite
            )
        );
        FrontendOnly(
            group.MapPost(
                "{organisationId}/{applicationId}/overseas-sites/{siteId}/revert",
                RevertOverseasSite
            )
        );
        FrontendOnly(
            group.MapPost(
                "{organisationId}/{applicationId}/overseas-sites/{siteId}/interim-site",
                AddInterimSite
            )
        );
        FrontendOnly(
            group.MapPost(
                "{organisationId}/{applicationId}/overseas-sites/{siteId}/bes-evidence/files",
                AddBesEvidenceFile
            )
        );
        FrontendOnly(
            group.MapPatch(
                "{organisationId}/{applicationId}/overseas-sites/{siteId}/bes-evidence",
                PatchBesEvidence
            )
        );
        FrontendOnly(
            group.MapDelete(
                "{organisationId}/{applicationId}/overseas-sites/{siteId}/bes-evidence/files/{fileId}",
                DeleteBesEvidenceFile
            )
        );
        FrontendOnly(
            group.MapPatch("{organisationId}/{applicationId}/bes-evidence", PatchBesEvidenceSection)
        );
        FrontendOnly(group.MapPost("{organisationId}/{applicationId}/submit", Submit));
        FrontendOnly(group.MapPost("{organisationId}/{applicationId}/resubmit", Resubmit));
        FrontendOnly(group.MapPost("{organisationId}/{applicationId}/withdraw", Withdraw));
        group
            .MapPost("case-management/{workItemId}/query", QueryFromCaseManagement)
            .RequireAuthorization(policy =>
                policy
                    .AddAuthenticationSchemes(CaseManagementAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
            );
        group
            .MapPost("case-management/{workItemId}/status", StatusChangedFromCaseManagement)
            .RequireAuthorization(policy =>
                policy
                    .AddAuthenticationSchemes(CaseManagementAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
            );
        // RA-448: regulator/caseworker actions, same CaseManagement caller identity as the
        // two routes above - not something the operator-facing frontend calls (AC7).
        group
            .MapPost(
                "{organisationId}/{applicationId}/registration-number",
                GenerateOrUpdateRegistrationNumber
            )
            .RequireAuthorization(policy =>
                policy
                    .AddAuthenticationSchemes(CaseManagementAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
            );
        group
            .MapPost(
                "{organisationId}/{applicationId}/accreditation-number",
                GenerateOrUpdateAccreditationNumber
            )
            .RequireAuthorization(policy =>
                policy
                    .AddAuthenticationSchemes(CaseManagementAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
            );
        // RA-469 AC18: regulator-scoped correction of one overseas site's recycling operation
        // codes, called by management-be (never the operator frontend) - same CaseManagement
        // caller identity as registration-number/accreditation-number above, not FrontendOnly.
        group
            .MapPatch(
                "{organisationId}/{applicationId}/overseas-sites/{siteId}/recycling-operations",
                PatchRecyclingOperations
            )
            .RequireAuthorization(policy =>
                policy
                    .AddAuthenticationSchemes(CaseManagementAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
            );
        FrontendOnly(group.MapPost("{organisationId}/{applicationId}/files", AddFile));
        FrontendOnly(
            group.MapDelete("{organisationId}/{applicationId}/files/{fileId}", DeleteFile)
        );
        FrontendOnly(
            group.MapPost("{organisationId}/{applicationId}/files/initiate", InitiateUpload)
        );
        FrontendOnly(
            group.MapPost(
                "{organisationId}/{applicationId}/files/bes-evidence/initiate",
                InitiateBesEvidenceUpload
            )
        );
        // CDP Uploader webhook callback — not frontend-authenticated, see FrontendOnly above.
        group.MapPost("files/upload-completed", UploadCompleted);
        FrontendOnly(
            group.MapGet(
                "{organisationId}/{applicationId}/files/{fileUploadId}/status",
                GetUploadStatus
            )
        );
    }

    // RA-252 / RA-415: an application in a terminal status (Withdrawn, Approved or Rejected)
    // must not be editable through any of the ordinary write endpoints, even if the frontend's
    // own session guard fails open or is bypassed.
    private static IResult? RejectIfTerminal(AccreditationApplicationModel application) =>
        application.ApplicationStatus
            is ApplicationStatus.Withdrawn
                or ApplicationStatus.Approved
                or ApplicationStatus.Rejected
            ? Results.Conflict(
                "Application is Approved, Rejected or Withdrawn and can no longer be edited."
            )
            : null;

    /// <summary>
    /// RA-475/RA-503: the ReEx lookup shared by every caller that needs the numeric
    /// organisation id (<c>OrganisationDto.OrgId</c>) behind a ReEx organisation id
    /// (<c>organisationId</c>, a UUID). Returns null on any lookup failure/absence -
    /// callers that have a fallback value of their own (see <see cref="ResolveOrgIdAsync"/>)
    /// apply it on top of this; <see cref="Submit"/> has none, so null propagates
    /// straight through to <c>AccreditationApplicationModel.OrgId</c>.
    /// </summary>
    private static async Task<int?> ResolveOrgNumberFromReExAsync(
        string organisationId,
        IReExApiAdapter reExAdapter,
        CancellationToken cancellationToken
    )
    {
        var reExResult = await reExAdapter.GetOrganisationNumberAsync(
            organisationId,
            cancellationToken
        );

        return reExResult is { IsSuccess: true, Value: { } orgNumber } ? orgNumber : null;
    }

    /// <summary>
    /// RA-475: resolve the numeric organisation id the <c>{OrgId:D6}</c> segment
    /// of a regulatory number is built from.
    ///
    /// ReEx is authoritative and is tried FIRST, because the caller cannot supply
    /// this value correctly: management-be only holds the ReEx organisation id,
    /// which is a UUID. It was passing that through <c>int.TryParse</c>, which
    /// fails for every genuinely-submitted application, and the resulting refusal
    /// surfaced in Case Management as "this application has changed since you
    /// opened it" on a determination that could never succeed.
    ///
    /// The caller's <c>OrgId</c> survives only as a fallback, for the two cases
    /// ReEx cannot answer: an organisation with no <c>orgId</c> recorded, and the
    /// seeded/stubbed fixtures whose organisation ids are already numeric and have
    /// no ReEx document behind them. A ReEx lookup FAILURE also falls back rather
    /// than failing the request outright - the caller-supplied value is no worse
    /// than the one this endpoint accepted unconditionally before.
    ///
    /// Returns null when neither source yields a value; the caller turns that into
    /// the same 400 a missing OrgId has always produced.
    /// </summary>
    private static async Task<int?> ResolveOrgIdAsync(
        string organisationId,
        GenerateOrUpdateRegulatoryNumberRequest request,
        IReExApiAdapter reExAdapter,
        CancellationToken cancellationToken
    )
    {
        var orgNumber = await ResolveOrgNumberFromReExAsync(
            organisationId,
            reExAdapter,
            cancellationToken
        );
        return orgNumber ?? request.OrgId;
    }

    // RA-448 AC1/AC5/AC6/AC7/AC9. Nation and Year stay caller-supplied (see
    // GenerateOrUpdateRegulatoryNumberRequest) - Nation because this backend has
    // no reliable source to derive it from, and Year per explicit product
    // direction (2026-08-19): no assumption about the year may be made on a
    // first-ever generate.
    //
    // RA-475: OrgId no longer is. ReEx IS a reliable source for it - an
    // organisation document carries its own numeric `orgId`, which is exactly
    // what the number format's {OrgId:D6} segment means - so it is resolved
    // here and the caller's value is only a fallback. See ResolveOrgIdAsync.
    private static async Task<IResult> GenerateOrUpdateRegistrationNumber(
        string organisationId,
        string applicationId,
        GenerateOrUpdateRegulatoryNumberRequest request,
        [AsParameters] RegulatoryNumberServices services
    )
    {
        var validation = await services.Validator.ValidateAsync(
            request,
            services.CancellationToken
        );
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await services.Persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        // AC5: idempotent - an existing number is returned unchanged unless the
        // caller explicitly asks to regenerate, so a retry never burns a sequence
        // value. Covers both a number this endpoint issued previously and one
        // already populated from ReEx at Seed time (AccreditationApplicationEndpoints.Seed).
        if (application.RegistrationReference is not null && !request.Regenerate)
            return Results.Ok(application);

        var orgIdResolution = await ResolveOrgIdAsync(
            organisationId,
            request,
            services.ReExAdapter,
            services.CancellationToken
        );

        if (
            request.Nation is not { } nationValue
            || !Enum.TryParse<Nation>(nationValue, ignoreCase: true, out var nation)
            || orgIdResolution is not { } orgId
            || request.Year is not { } year
        )
            return Results.BadRequest(
                "Nation, OrgId and Year are required to generate a registration number."
            );

        var newNumber = await services.Generator.GenerateAsync(
            new RegulatoryNumberSpec
            {
                Type = NumberType.Registration,
                Nation = nation,
                IsExporter = application.IsExporter,
                OrgId = orgId,
                Material = application.MaterialType,
                GlassRecyclingProcess = application.GlassRecyclingProcess,
                Year = year,
            },
            services.CancellationToken
        );

        // Regenerate: keep the prior number in the audit trail, never reuse/reissue it
        // (AC5). A first-ever generate has no prior number to preserve.
        if (application.RegistrationReference is { } previousNumber)
            application.PreviousRegistrationNumbers.Add(previousNumber);

        application.RegistrationReference = newNumber;
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await services.Persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update registration number.")
            : Results.Ok(updated);
    }

    // RA-448 AC1/AC5/AC6/AC7/AC9. Regenerate here is NOT the same mechanism as
    // registration's: "reapply for accreditation" increments only the existing
    // number's YY segment in place - it never draws from the atomic counter and
    // never mutates regulatoryNumberSequences. A first-ever generate still goes
    // through the normal counter-backed generator, same as registration.
    private static async Task<IResult> GenerateOrUpdateAccreditationNumber(
        string organisationId,
        string applicationId,
        GenerateOrUpdateRegulatoryNumberRequest request,
        [AsParameters] RegulatoryNumberServices services
    )
    {
        var validation = await services.Validator.ValidateAsync(
            request,
            services.CancellationToken
        );
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await services.Persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        // AC9: registration precedes and is independent of accreditation - an
        // accreditation number is never issued with nothing to accredit against.
        if (application.RegistrationReference is null)
            return Results.Conflict(
                "Application has no registration number yet; an accreditation number cannot be issued."
            );

        // AC5: idempotent - an existing number is returned unchanged unless the
        // caller explicitly asks to regenerate.
        if (application.AccreditationReference is not null && !request.Regenerate)
            return Results.Ok(application);

        string newNumber;
        if (application.AccreditationReference is { } numberToReapply && request.Regenerate)
        {
            // "Reapply for accreditation": pure string transform, no counter draw.
            newNumber = IncrementYear(numberToReapply);
        }
        else
        {
            var orgIdResolution = await ResolveOrgIdAsync(
                organisationId,
                request,
                services.ReExAdapter,
                services.CancellationToken
            );

            if (
                request.Nation is not { } nationValue
                || !Enum.TryParse<Nation>(nationValue, ignoreCase: true, out var nation)
                || orgIdResolution is not { } orgId
                || request.Year is not { } year
            )
                return Results.BadRequest(
                    "Nation, OrgId and Year are required to generate an accreditation number."
                );

            newNumber = await services.Generator.GenerateAsync(
                new RegulatoryNumberSpec
                {
                    Type = NumberType.Accreditation,
                    Nation = nation,
                    IsExporter = application.IsExporter,
                    OrgId = orgId,
                    Material = application.MaterialType,
                    GlassRecyclingProcess = application.GlassRecyclingProcess,
                    Year = year,
                },
                services.CancellationToken
            );
        }

        // Regenerate (either mechanism): keep the prior number in the audit trail,
        // never reuse/reissue it (AC5). A first-ever generate has no prior number.
        if (application.AccreditationReference is { } previousNumber)
            application.PreviousAccreditationNumbers.Add(previousNumber);

        application.AccreditationReference = newNumber;
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await services.Persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update accreditation number.")
            : Results.Ok(updated);
    }

    // Bundles GenerateOrUpdateRegistrationNumber/GenerateOrUpdateAccreditationNumber's
    // DI-service/framework parameters under one [AsParameters] argument so each handler
    // stays under Sonar's 7-parameter limit (S107) - same reasoning as
    // RecyclingOperationsServices above. A positional record, not bare `{ get; init; }`
    // auto-properties, for the same S3459/S1144 reason given there.
    private sealed record RegulatoryNumberServices(
        IAccreditationApplicationPersistence Persistence,
        IRegulatoryNumberGenerator Generator,
        IValidator<GenerateOrUpdateRegulatoryNumberRequest> Validator,
        IReExApiAdapter ReExAdapter,
        CancellationToken CancellationToken
    );

    // RA-469 AC18 / RA-470 gap 2: application/material-type-aware OperationCodes checks the
    // request-shape validators (PatchRecyclingOperationsRequestValidator,
    // PromoteOverseasSiteRequestValidator) can't do alone - see RecyclingOperationCodes' own doc
    // comment - shared by PatchRecyclingOperations (regulator-driven, below) and
    // UpdateOverseasSite (operator-driven, RA-470) so the two never drift apart. Needs the loaded
    // site (for InterimSite) and the application's MaterialType, neither of which a request-shape
    // validator has access to. Returns null when the codes are valid.
    //
    // enforceInterimSiteRequirement gates the AC11 sub-check only. PatchRecyclingOperations is a
    // standalone codes-only edit, so R12/R13 must already have their InterimSite in place at the
    // moment it's called. UpdateOverseasSite backs the operator's Change wizard, which - exactly
    // like the existing Add/Promote wizard entry points (neither of which calls this method at
    // all) - submits the site PATCH *before* redirecting into the separate interim-site sub-wizard
    // when requiresInterimSite() is true; enforcing AC11 there would 400 that handoff every time.
    // Gap 1's own check below (site.InterimSite is not null but request drops R12/R13) still
    // applies unconditionally on UpdateOverseasSite - it protects the opposite, already-attached
    // direction, which this parameter doesn't touch.
    private static IResult? ValidateOperationCodesForSite(
        MaterialType materialType,
        List<string> operationCodes,
        OverseasSiteModel site,
        int siteId,
        bool enforceInterimSiteRequirement = true
    )
    {
        // A code not offered for this application's material type is rejected even though it's a
        // member of the overall valid-code set.
        var applicableCodes = RecyclingOperationCodes.ApplicableCodesFor(materialType);
        if (operationCodes.Any(c => !applicableCodes.Contains(c)))
            return Results.BadRequest(
                $"OperationCodes must each be applicable to material type '{materialType}': {string.Join(", ", applicableCodes)}."
            );

        // AC11: R12/R13 describe an operation performed in relation to an associated interim
        // site, so an ORS with none can't carry them.
        if (
            enforceInterimSiteRequirement
            && operationCodes.Any(RecyclingOperationCodes.CodesRequiringAccompaniment.Contains)
            && site.InterimSite is null
        )
            return Results.BadRequest(
                $"R12 and R13 require an associated interim site on overseas site '{siteId}'."
            );

        return null;
    }

    // RA-469 AC18: updates OverseasSiteModel.OperationCodes for exactly one site - nothing else
    // on the application (not SectionStatus, not DateLastEdited, not the submit/resubmit-only
    // Versions snapshot) is touched, since this is a one-off regulator correction outside the
    // operator's normal edit flow, not an operator edit itself. Deliberately does NOT go through
    // AccreditationApplicationSections.IsSectionEditable (unlike AddInterimSite) for the same
    // reason - that gate exists to protect the operator's own Queried/section workflow, which
    // this correction must not disturb either way.
    //
    // RA-469 AC15/AC19 (epr-register-enrol-backend-9kr): the audit record is written after a
    // successful update, never on a validation/404/409 short-circuit above. cdp_user_id/
    // cdp_user_name come from CaseManagementAuthenticationHandler's claims (in turn from the
    // x-cdp-user-id/x-cdp-user-name request headers) - both are absent on the Development
    // header-trust bypass (no shared secret configured), so FindFirst(...)?.Value defaults to
    // string.Empty rather than throwing when identity is unavailable. The audit write is
    // deliberately NOT wrapped in try/catch: unlike AddInterimSite's best-effort ManagementBe
    // courtesy notification, AC19 requires every successful edit to be recorded, so a failed
    // audit write should surface as a 500 (via the app's exception handler) rather than silently
    // leaving an unaudited change in place.
    private static async Task<IResult> PatchRecyclingOperations(
        string organisationId,
        string applicationId,
        int siteId,
        PatchRecyclingOperationsRequest request,
        [AsParameters] RecyclingOperationsServices services
    )
    {
        var validation = await services.Validator.ValidateAsync(
            request,
            services.CancellationToken
        );
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await services.Persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        var site = application.OverseasSites?.Sites.FirstOrDefault(s => s.SiteId == siteId);
        if (site is null)
            return Results.NotFound();

        if (
            ValidateOperationCodesForSite(
                application.MaterialType,
                request.OperationCodes,
                site,
                siteId
            ) is
            { } codesError
        )
            return codesError;

        // Snapshotted before mutation, as an independent list - not aliased with the request's
        // own OperationCodes below (RecyclingOperationsAuditPersistence's tests require the
        // before/after lists to remain independent).
        var beforeCodes = site.OperationCodes.ToList();
        site.OperationCodes = request.OperationCodes;

        var updated = await services.Persistence.UpdateAsync(application);
        if (updated is null)
            return Results.Problem("Failed to update recycling operations.");

        await services.AuditPersistence.RecordAsync(
            new RecyclingOperationsAuditRecord
            {
                CdpUserId =
                    services.HttpContext.User.FindFirst("cdp_user_id")?.Value ?? string.Empty,
                CdpUserName =
                    services.HttpContext.User.FindFirst("cdp_user_name")?.Value ?? string.Empty,
                OrganisationId = organisationId,
                ApplicationId = applicationId,
                SiteId = siteId,
                BeforeCodes = beforeCodes,
                AfterCodes = request.OperationCodes,
            },
            services.CancellationToken
        );

        // `site` is the same object already mutated above (and, on the real Mongo-backed
        // persistence, `updated` is that same `application` reference echoed back on success -
        // see AccreditationApplicationPersistence.UpdateAsync) - returning it directly avoids
        // re-deriving it from `updated` with a null-forgiving lookup, matching AddInterimSite's
        // own pattern of returning the local object it already holds.
        return Results.Ok(site);
    }

    // Bundles PatchRecyclingOperations' DI-service/framework parameters under one
    // [AsParameters] argument so the handler itself stays under Sonar's 7-parameter
    // limit (S107) - same reasoning as RegulatoryNumberSpec, adapted for a minimal-API
    // handler (whose route/body parameters can't be bundled the same way, since ASP.NET
    // binds each top-level parameter from a different source). A positional record,
    // not bare `{ get; init; }` auto-properties: ASP.NET's [AsParameters] binding
    // constructs this via reflection either way, invisible to static analysis, but a
    // primary-constructor assignment reads as a real assignment to Sonar (S3459/S1144)
    // the same way every other DI-constructed class in this file already does - a bare
    // init-only auto-property with no constructor at all does not.
    private sealed record RecyclingOperationsServices(
        IAccreditationApplicationPersistence Persistence,
        IValidator<PatchRecyclingOperationsRequest> Validator,
        IRecyclingOperationsAuditPersistence AuditPersistence,
        HttpContext HttpContext,
        CancellationToken CancellationToken
    );

    // "Reapply for accreditation" (AC5, RA-448): increments only the YY segment
    // (index 1-2 of the fixed {R|A}{YY}{AgencyType}{OrgID}{Sequence}{Material}
    // format) against whatever YY the number currently holds - not the calendar
    // year at reapply time - leaving every other segment byte-identical.
    private static string IncrementYear(string existingNumber)
    {
        var currentYear = int.Parse(existingNumber.Substring(1, 2));
        var nextYear = (currentYear + 1) % 100;
        return string.Concat(
            existingNumber.AsSpan(0, 1),
            nextYear.ToString("D2"),
            existingNumber.AsSpan(3)
        );
    }

    private static readonly System.Text.RegularExpressions.Regex FilenameIsSafe = new(
        @"^[^\x00-\x1f<>:""/\\|?*]+$",
        System.Text.RegularExpressions.RegexOptions.None,
        TimeSpan.FromMilliseconds(100)
    );

    // H6 (2026-08-08 pentest report) fix: file identity/scan-result/S3 location must come
    // from the server-held PendingUploadService record, populated only by the real
    // CDP-uploader webhook callback — never trusted verbatim from the client request body.
    private static IResult? TryResolveScannedFile(
        string fileUploadId,
        IPendingUploadService pendingUploadService,
        out CdpCallbackFile scannedFile
    )
    {
        var status = pendingUploadService.GetStatus(fileUploadId);
        var file = status.Form?.File;
        if (
            status.ProcessingStatus != "validated"
            || file is null
            || string.IsNullOrWhiteSpace(file.FileId)
            || string.IsNullOrWhiteSpace(file.Filename)
            || file.Filename.Length > 255
            || !FilenameIsSafe.IsMatch(file.Filename)
            || string.IsNullOrWhiteSpace(file.S3Key)
        )
        {
            scannedFile = null!;
            return Results.UnprocessableEntity(
                "No completed, scanned upload was found for the supplied FileUploadId."
            );
        }

        scannedFile = file;
        return null;
    }

    private static async Task<IResult> Seed(
        string organisationId,
        string registrationId,
        string materialType,
        SeedRequest request,
        IAccreditationApplicationPersistence persistence,
        IReExApiAdapter reExAdapter,
        IValidator<SeedRequest> validator
    )
    {
        if (!Enum.TryParse<MaterialType>(materialType, ignoreCase: true, out var materialTypeEnum))
            return Results.BadRequest("Invalid material type.");

        if (
            string.IsNullOrWhiteSpace(registrationId)
            || registrationId.Equals("undefined", StringComparison.OrdinalIgnoreCase)
            || registrationId.Equals("null", StringComparison.OrdinalIgnoreCase)
        )
            return Results.BadRequest("Invalid registration id.");

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        // Idempotency check before the ReEx call — duplicate seeds return the existing document.
        // RA-357: only a *live* application short-circuits the seed. A withdrawn one must not block
        // starting again for the same accreditation year, so withdrawn records are excluded here;
        // they are retained untouched for audit (RA-252 keeps them read-only), and the restart falls
        // through to create a brand new application exactly as a first-time seed would.
        // GetByOrganisationAsync applies no server-side sort, so order the candidates explicitly
        // (NewestFirst — the shared rule, also used by GetList) rather than relying on incidental
        // storage order to decide which one is "the live one".
        //
        // At most one live application per (org, registrationId, materialType, year) is
        // BEST-EFFORT, not an invariant: this is a read-then-create with no transaction and no
        // unique index, and "Start new accreditation application" is now a user-triggered create,
        // so two concurrent seeds can both pass this check and both create — leaving a second
        // live record that still shows up in GET /{organisationId}. Consumers must therefore
        // tolerate duplicates and apply this same newest-first rule rather than assuming
        // uniqueness.
        // A unique partial index would be the real fix, but it is not available today:
        // MongoService.EnsureIndexes (Utils/Mongo/MongoService.cs) builds its index models, logs
        // that it is ensuring them, and then has the Collection.Indexes.CreateMany call commented
        // out — so no index in this service is ever created, in any environment. A unique index
        // added here would be silently inert: worse than none, because it would read as a
        // guarantee that is not enforced at runtime.
        var existing = (await persistence.GetByOrganisationAsync(organisationId))
            .Where(a =>
                a.RegistrationId == registrationId
                && a.MaterialType == materialTypeEnum
                && a.Year == request.Year
                && a.ApplicationStatus != ApplicationStatus.Withdrawn
            )
            .NewestFirst()
            .FirstOrDefault();
        if (existing is not null)
            return Results.Ok(existing);

        var adapterResult = await reExAdapter.GetAccreditationAsync(
            organisationId,
            registrationId,
            materialTypeEnum,
            request.Year - 1
        );

        if (!adapterResult.IsSuccess)
            return adapterResult.IsNotFound
                ? Results.NotFound()
                : Results.Problem(statusCode: adapterResult.IsUpstreamFailure ? 502 : 503);

        var priorYearData = adapterResult.Value!;

        var application = new AccreditationApplicationModel
        {
            OrganisationId = organisationId,
            OrganisationName = priorYearData.OrganisationName,
            Year = request.Year,
            RegistrationId = registrationId,
            IsExporter = priorYearData.IsExporter,
            SiteAddress = priorYearData.SiteAddress,
            CompanyRegisterAddressPostcode = priorYearData.CompanyRegisterAddressPostcode,
            CompanyRegisteredAddress = priorYearData.CompanyRegisteredAddress,
            CompaniesHouseNumber = priorYearData.CompaniesHouseNumber,
            PermitNumbers = priorYearData.PermitNumbers,
            SubmitterContactDetails = priorYearData.SubmitterContactDetails is { } submitterContact
                ? new SubmitterContactDetailsModel
                {
                    FullName = submitterContact.FullName,
                    Email = submitterContact.Email,
                    Phone = submitterContact.Phone,
                    JobTitle = submitterContact.JobTitle,
                }
                : null,
            WasteProcessingType = priorYearData.WasteProcessingType,
            RegistrationReference = priorYearData.RegistrationReference,
            MaterialType = materialTypeEnum,
            GlassRecyclingProcess = priorYearData.GlassRecyclingProcess,
            ApplicationStatus = ApplicationStatus.Saved,
            SourceReExAccreditationId = priorYearData.AccreditationId,
            SourceYear = request.Year - 1,
            OverseasSites = priorYearData.IsExporter
                ? new AccreditationApplicationOverseasSites { Sites = priorYearData.OverseasSites }
                : null,
        };

        if (priorYearData.Prns != null)
        {
            application.Prns = new AccreditationApplicationPrns
            {
                PlannedTonnageBand = priorYearData.Prns.PlannedTonnageBand,
                // RA-292 AC03: prior-year contacts carried over from ReEx existed before this
                // application, so they are never flagged new to the regulator.
                Authorisers = PrnsAuthoriserMerge.MarkAsExisting(priorYearData.Prns.Authorisers),
                SectionStatus = SectionStatus.NotStarted,
            };
        }

        if (priorYearData.BusinessPlan != null)
        {
            application.BusinessPlan = new AccreditationApplicationBusinessPlan
            {
                NewInfrastructurePercent = priorYearData.BusinessPlan.NewInfrastructurePercent,
                PriceSupportPercent = priorYearData.BusinessPlan.PriceSupportPercent,
                BusinessCollectionsPercent = priorYearData.BusinessPlan.BusinessCollectionsPercent,
                CommunicationsPercent = priorYearData.BusinessPlan.CommunicationsPercent,
                NewMarketsPercent = priorYearData.BusinessPlan.NewMarketsPercent,
                NewUsesPercent = priorYearData.BusinessPlan.NewUsesPercent,
                // RA-456: ReEx already sends this category ("Activities or investment not covered
                // by the other categories") and HttpReExApiAdapter already maps it into
                // ReExBusinessPlanDto.OtherPercent — this copy was the missing link that silently
                // dropped it before it ever reached the domain model.
                OtherPercent = priorYearData.BusinessPlan.OtherPercent,
                SectionStatus = SectionStatus.NotStarted,
            };
        }

        application.DateLastEdited = application.CreatedAt;

        var created = await persistence.CreateAsync(application);
        if (created is null)
            return Results.Problem("Failed to create accreditation application.");

        return Results.Created(
            $"/api/v1/accreditation-applications/{organisationId}/{created.Id}",
            created
        );
    }

    private static async Task<IResult> GetList(
        string organisationId,
        IAccreditationApplicationPersistence persistence
    )
    {
        // RA-357: (organisationId, registrationId, materialType, year) is now one-to-many — a
        // restart after a withdrawal adds a second record for the same key. GetByOrganisationAsync
        // applies no server-side sort, so order here with NewestFirst — the same shared rule Seed
        // uses to pick the live application. That gives every consumer a stable list and makes a
        // naive "first match wins" client land on the newest record rather than an arbitrary one;
        // FE #204 relies on exactly that. Withdrawn records are deliberately NOT filtered out —
        // consumers legitimately need to display them; choosing the live one is the caller's
        // decision.
        var applications = (await persistence.GetByOrganisationAsync(organisationId)).NewestFirst();
        return Results.Ok(applications);
    }

    private static async Task<IResult> GetById(
        string organisationId,
        string applicationId,
        IAccreditationApplicationPersistence persistence,
        ICaseWorkingApiAdapter caseWorkingAdapter,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        // Skip the round-trip entirely when there is nothing to look up (e.g. older
        // applications submitted before the work-item id was persisted, or applications
        // never submitted at all). Defence in depth otherwise: GetNotificationStatusAsync
        // already guarantees it never throws, but this lookup must never be able to fail
        // the response regardless of adapter implementation (RA102-j7s).
        if (application.CaseManagementWorkItemId is not null)
        {
            try
            {
                var notificationStatus = await caseWorkingAdapter.GetNotificationStatusAsync(
                    application,
                    cancellationToken
                );
                application.NotificationStatus = notificationStatus.NotificationStatus;
                application.DueDate = notificationStatus.SlaDueDate;
            }
            catch (Exception ex)
            {
                loggerFactory
                    .CreateLogger("AccreditationApplicationEndpoints")
                    .LogWarning(
                        ex,
                        "Failed to resolve notification status for applicationId={ApplicationId}",
                        applicationId
                    );
            }
        }

        return Results.Ok(application);
    }

    private static async Task<IResult> PatchPrns(
        string organisationId,
        string applicationId,
        PatchPrnsRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<PatchPrnsRequest> validator
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.Prns.SectionStatus
            )
        )
            return Results.Conflict(
                "PRNs section is not editable in the application's current status."
            );

        if (request.PlannedTonnageBand.HasValue)
            application.Prns.PlannedTonnageBand = request.PlannedTonnageBand;

        // RA-292 AC03: IsNew is re-derived server-side against the persisted list; whatever the
        // client sent on each authoriser is discarded.
        if (request.Authorisers != null)
            application.Prns.Authorisers = PrnsAuthoriserMerge.Merge(
                application.Prns.Authorisers,
                request.Authorisers
            );

        if (application.Prns.SectionStatus != SectionStatus.Queried)
        {
            var (status, error) = SectionStatusService.ResolveRequestedStatus(
                request.SectionStatus,
                () => SectionStatusService.ComputePrns(application.Prns),
                "PRNs"
            );
            if (error is not null)
                return Results.UnprocessableEntity(error);
            application.Prns.SectionStatus = status!.Value;
        }
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update PRNs section.")
            : Results.Ok(updated);
    }

    private static async Task<IResult> PatchTonnage(
        string organisationId,
        string applicationId,
        PatchTonnageRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<PatchTonnageRequest> validator
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.Prns.SectionStatus
            )
        )
            return Results.Conflict(
                "PRNs section is not editable in the application's current status."
            );

        if (request.PlannedTonnageBand.HasValue)
            application.Prns.PlannedTonnageBand = request.PlannedTonnageBand;

        // RA-292 AC03: same server-side derivation as PatchPrns — the tonnage-authority journey
        // PATCHes the whole authoriser list through here.
        if (request.Authorisers != null)
            application.Prns.Authorisers = PrnsAuthoriserMerge.Merge(
                application.Prns.Authorisers,
                request.Authorisers
            );

        if (application.Prns.SectionStatus != SectionStatus.Queried)
        {
            var (status, error) = SectionStatusService.ResolveRequestedStatus(
                request.SectionStatus,
                () => SectionStatusService.ComputePrns(application.Prns),
                "PRNs"
            );
            if (error is not null)
                return Results.UnprocessableEntity(error);
            application.Prns.SectionStatus = status!.Value;
        }
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null ? Results.Problem("Failed to update tonnage.") : Results.Ok(updated);
    }

    private static async Task<IResult> PatchBusinessPlan(
        string organisationId,
        string applicationId,
        PatchBusinessPlanRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<PatchBusinessPlanRequest> validator
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.UnprocessableEntity(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.BusinessPlan.SectionStatus
            )
        )
            return Results.Conflict(
                "Business plan section is not editable in the application's current status."
            );

        var bp = application.BusinessPlan;
        if (request.NewInfrastructurePercent.HasValue)
            bp.NewInfrastructurePercent = request.NewInfrastructurePercent;
        if (request.PriceSupportPercent.HasValue)
            bp.PriceSupportPercent = request.PriceSupportPercent;
        if (request.BusinessCollectionsPercent.HasValue)
            bp.BusinessCollectionsPercent = request.BusinessCollectionsPercent;
        if (request.CommunicationsPercent.HasValue)
            bp.CommunicationsPercent = request.CommunicationsPercent;
        if (request.NewMarketsPercent.HasValue)
            bp.NewMarketsPercent = request.NewMarketsPercent;
        if (request.NewUsesPercent.HasValue)
            bp.NewUsesPercent = request.NewUsesPercent;
        if (request.OtherPercent.HasValue)
            bp.OtherPercent = request.OtherPercent;

        if (request.NewInfrastructureDetail != null)
            bp.NewInfrastructureDetail = request.NewInfrastructureDetail;
        if (request.PriceSupportDetail != null)
            bp.PriceSupportDetail = request.PriceSupportDetail;
        if (request.BusinessCollectionsDetail != null)
            bp.BusinessCollectionsDetail = request.BusinessCollectionsDetail;
        if (request.CommunicationsDetail != null)
            bp.CommunicationsDetail = request.CommunicationsDetail;
        if (request.NewMarketsDetail != null)
            bp.NewMarketsDetail = request.NewMarketsDetail;
        if (request.NewUsesDetail != null)
            bp.NewUsesDetail = request.NewUsesDetail;
        if (request.OtherDetail != null)
            bp.OtherDetail = request.OtherDetail;

        if (bp.SectionStatus != SectionStatus.Queried)
        {
            var (status, error) = SectionStatusService.ResolveRequestedStatus(
                request.SectionStatus,
                () => SectionStatusService.ComputeBusinessPlan(bp),
                "Business plan"
            );
            if (error is not null)
                return Results.UnprocessableEntity(error);
            bp.SectionStatus = status!.Value;
        }
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update business plan section.")
            : Results.Ok(updated);
    }

    private static async Task<IResult> PatchSamplingPlan(
        string organisationId,
        string applicationId,
        PatchSamplingPlanRequest request,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.SamplingPlan.SectionStatus
            )
        )
            return Results.Conflict(
                "Sampling plan section is not editable in the application's current status."
            );

        if (request.Files != null)
            application.SamplingPlan.Files = request.Files;

        if (application.SamplingPlan.SectionStatus != SectionStatus.Queried)
        {
            var (status, error) = SectionStatusService.ResolveRequestedStatus(
                request.SectionStatus,
                () => SectionStatusService.ComputeSamplingPlan(application.SamplingPlan),
                "Sampling plan"
            );
            if (error is not null)
                return Results.UnprocessableEntity(error);
            application.SamplingPlan.SectionStatus = status!.Value;
        }
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update sampling plan section.")
            : Results.Ok(updated);
    }

    private static async Task<IResult> PatchOverseasSites(
        string organisationId,
        string applicationId,
        PatchOverseasSitesRequest request,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.OverseasSites?.SectionStatus ?? SectionStatus.NotStarted
            )
        )
            return Results.Conflict(
                "Overseas sites section is not editable in the application's current status."
            );

        if (application.OverseasSites is null)
            application.OverseasSites = new AccreditationApplicationOverseasSites();

        // RA-292 AC01/AC02: isNewSite (site and interim) is re-derived server-side against the
        // persisted list; whatever the client sent for it is discarded.
        if (request.Sites != null)
            application.OverseasSites.Sites = OverseasSiteMerge.Merge(
                application.OverseasSites.Sites,
                request.Sites
            );

        if (
            RecomputeOverseasSitesSectionStatus(application.OverseasSites, request.SectionStatus) is
            { } statusError
        )
            return statusError;

        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update overseas sites.")
            : Results.Ok(updated);
    }

    private static async Task<IResult> AddOverseasSite(
        string organisationId,
        string applicationId,
        AddOverseasSiteRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<AddOverseasSiteRequest> validator,
        ICaseWorkingApiAdapter caseWorkingAdapter,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.OverseasSites?.SectionStatus ?? SectionStatus.NotStarted
            )
        )
            return Results.Conflict(
                "Overseas sites section is not editable in the application's current status."
            );

        application.OverseasSites ??= new AccreditationApplicationOverseasSites();

        const int maxSitesPerApplication = 500;
        if (application.OverseasSites.Sites.Count >= maxSitesPerApplication)
            return Results.UnprocessableEntity(
                $"A maximum of {maxSitesPerApplication} overseas sites is permitted per application."
            );

        var (errorResult, newSite, updated) = await TryAddOverseasSiteWithGeneratedIdAsync(
            persistence,
            organisationId,
            applicationId,
            application,
            request
        );
        if (errorResult is not null || newSite is null || updated is null)
            return errorResult ?? Results.Problem("Failed to add overseas site.");

        // Courtesy notification to ManagementBe — must never fail this response (RA-294 AC05 /
        // RA-297 AC04). Same guard/comment style as GetById (RA102-j7s): skip the round-trip
        // entirely when there is nothing to notify, and defend in depth around the call even
        // though NotifySiteAddedAsync itself already guarantees it never throws.
        if (updated.CaseManagementWorkItemId is not null)
        {
            try
            {
                await caseWorkingAdapter.NotifySiteAddedAsync(
                    updated,
                    siteType: "ors",
                    orsId: newSite.OrsId ?? string.Empty,
                    siteNumber: null,
                    isNewSite: newSite.IsNewSite,
                    cancellationToken: cancellationToken
                );
            }
            catch (Exception ex)
            {
                loggerFactory
                    .CreateLogger("AccreditationApplicationEndpoints")
                    .LogWarning(
                        ex,
                        "Failed to notify ManagementBe of new overseas site for applicationId={ApplicationId}",
                        applicationId
                    );
            }
        }

        return Results.Created(string.Empty, newSite);
    }

    // RA-482: pulled out of AddOverseasSite to keep that method's cognitive complexity down —
    // this owns the whole generate-write-retry loop, including the capacity guard and the
    // re-fetch-and-retry path, and reports back either a terminal error IResult or the
    // successfully persisted site/application pair (never a mix of the two).
    private static async Task<(
        IResult? ErrorResult,
        OverseasSiteModel? NewSite,
        AccreditationApplicationModel? Updated
    )> TryAddOverseasSiteWithGeneratedIdAsync(
        IAccreditationApplicationPersistence persistence,
        string organisationId,
        string applicationId,
        AccreditationApplicationModel application,
        AddOverseasSiteRequest request
    )
    {
        // RA-482: OrsId is server-generated (max existing numeric id + 1, zero-padded, scoped by
        // RegistrationId across every application under it, falling back to just this
        // application when no RegistrationId is set yet) rather than accepted from the client.
        // The write is guarded so a concurrent AddOverseasSite call under the same registration
        // can't silently produce a duplicate: UpdateIfOrsIdAbsentAsync only persists if nothing
        // else already inserted that exact id, and a failed attempt retries with a freshly
        // computed id. Bounded, not unlimited, so a genuinely stuck conflict fails loudly.
        const int maxOrsIdAttempts = 3;

        // Callers already guarantee this before calling in, but that guarantee doesn't cross
        // the method boundary for the compiler's nullable flow analysis -- assert it locally too.
        application.OverseasSites ??= new AccreditationApplicationOverseasSites();

        for (var attempt = 1; attempt <= maxOrsIdAttempts; attempt++)
        {
            var scope = await OrsIdScope(persistence, organisationId, application);
            var generated = OrsIdGenerator.GenerateNext(scope);
            if (generated.CapacityExceeded)
                return (
                    Results.UnprocessableEntity(
                        "This registration has reached the maximum of 999 overseas sites."
                    ),
                    null,
                    null
                );

            var newSite = BuildOverseasSite(application.OverseasSites, generated.OrsId!, request);

            application.OverseasSites.Sites.Add(newSite);
            RecomputeOverseasSitesSectionStatus(application.OverseasSites);
            application.DateLastEdited = DateTime.UtcNow;

            if (application.ApplicationStatus == ApplicationStatus.Saved)
                application.ApplicationStatus = ApplicationStatus.Started;

            var updated = await persistence.UpdateIfOrsIdAbsentAsync(application, generated.OrsId!);
            if (updated is not null)
                return (null, newSite, updated);

            // Lost the race to a concurrent writer — re-fetch the now-current document and retry
            // with a freshly computed id rather than risking a duplicate.
            var refetched = await persistence.GetByIdAsync(organisationId, applicationId);
            if (refetched is null)
                return (Results.NotFound(), null, null);
            application = refetched;
            application.OverseasSites ??= new AccreditationApplicationOverseasSites();
        }

        return (
            Results.Conflict(
                "Could not allocate a unique ORS id after several attempts; please retry."
            ),
            null,
            null
        );
    }

    private static OverseasSiteModel BuildOverseasSite(
        AccreditationApplicationOverseasSites overseasSites,
        string orsId,
        AddOverseasSiteRequest request
    ) =>
        new()
        {
            SiteId = NextSiteId(overseasSites),
            OrsId = orsId,
            SiteName = request.SiteName,
            AddressLine1 = request.AddressLine1,
            AddressLine2 = request.AddressLine2,
            TownOrCity = request.TownOrCity,
            Country = request.Country,
            SiteAddress = string.Join(
                ", ",
                new[] { request.AddressLine1, request.TownOrCity, request.Country }.Where(s =>
                    !string.IsNullOrWhiteSpace(s)
                )
            ),
            Coordinates = request.Coordinates,
            ContactName = request.ContactName,
            ContactEmail = request.ContactEmail,
            ContactPhone = request.ContactPhone,
            OperationCodes = request.OperationCodes,
            Code1 = request.Code1,
            Code2 = request.Code2,
            Code3 = request.Code3,
            RepatriatedLoads = request.RepatriatedLoads,
            ConditionsOfExport = request.ConditionsOfExport,
            IsEu = CountryClassifications.IsEu(request.Country),
            IsOecd = CountryClassifications.IsOecd(request.Country),
            // The one place an ORS is genuinely created by the operator, so the one place the
            // regulator's "new" badge is switched on (RA-292 AC01). Sites arriving from ReEx are
            // pre-existing and stay false.
            IsNewSite = true,
        };

    // RA-482: the OrsId allocation scope. A registration is established once and renewed
    // annually via a new AccreditationApplicationModel each year, so RegistrationId (not the
    // non-nullable OrganisationId) is what actually identifies "this registration" across every
    // year's application — see the RA-482 lesson. GetByOrganisationAsync is the only query
    // available (no direct by-RegistrationId lookup), so every application for the org is
    // fetched and filtered in memory. Every OrsId string is included regardless of Selected
    // status or origin (operator-added or ReEx-seeded) — a deselected site keeps its id
    // permanently. When RegistrationId is null (no registration established yet for this
    // application), there is by definition no cross-year history to scope against, so this
    // falls back to just the current application's own sites.
    private static async Task<IEnumerable<string?>> OrsIdScope(
        IAccreditationApplicationPersistence persistence,
        string organisationId,
        AccreditationApplicationModel application
    )
    {
        if (application.RegistrationId is null)
            return (application.OverseasSites?.Sites ?? []).Select(s => s.OrsId);

        var allForOrganisation = await persistence.GetByOrganisationAsync(organisationId);
        return allForOrganisation
            .Where(a => a.RegistrationId == application.RegistrationId)
            .SelectMany(a => a.OverseasSites?.Sites ?? [])
            .Select(s => s.OrsId);
    }

    // Site numbers must be unique application-wide across both ORS sites and their nested
    // interim sites (RA-294), so the next id is the max across both, not just the ORS list.
    private static int NextSiteId(AccreditationApplicationOverseasSites overseasSites)
    {
        var maxSiteId = 0;
        foreach (var site in overseasSites.Sites)
        {
            if (site.SiteId > maxSiteId)
                maxSiteId = site.SiteId;
            if (site.InterimSite is not null && site.InterimSite.SiteId > maxSiteId)
                maxSiteId = site.InterimSite.SiteId;
        }
        return maxSiteId + 1;
    }

    // Shared by AddOverseasSite/PatchOverseasSites/PromoteOverseasSite/RevertOverseasSite so
    // the four don't each duplicate the Queried guard.
    //
    // Auto-compute (requestedStatus omitted, as AddOverseasSite/PromoteOverseasSite/
    // RevertOverseasSite always do) is deliberately binary (Completed/NotStarted only, no
    // InProgress): this section has no partial-completion concept like BusinessPlan/SamplingPlan
    // do when left to compute itself — a selected site means the section is done, matching
    // AccreditationApplicationSections.ComputeCurrentStatus, which the resubmit-after-query flow
    // uses as the source of truth for this section. So AddOverseasSite reporting Completed on the
    // very first site (rather than the InProgress it used to report before it was routed through
    // this helper) is intended, not a regression.
    //
    // RA-496: PatchOverseasSites is the one call site that can pass an explicit requestedStatus —
    // the operator's save intent from the task-list buttons — so it alone gets an InProgress
    // option ("save and come back later"), gated the same way every other section's Completed
    // intent is: rejected (not silently downgraded) if no site is actually selected yet. Returns
    // the conflict/validation IResult to short-circuit on, or null on success.
    private static IResult? RecomputeOverseasSitesSectionStatus(
        AccreditationApplicationOverseasSites overseasSites,
        SectionStatus? requestedStatus = null
    )
    {
        if (overseasSites.SectionStatus == SectionStatus.Queried)
            return null;

        var hasSelectedSite = overseasSites.Sites.Any(s => s.Selected);

        if (!requestedStatus.HasValue)
        {
            overseasSites.SectionStatus = hasSelectedSite
                ? SectionStatus.Completed
                : SectionStatus.NotStarted;
            return null;
        }

        if (
            requestedStatus.Value != SectionStatus.InProgress
            && requestedStatus.Value != SectionStatus.Completed
        )
            return Results.UnprocessableEntity(
                "Overseas sites section status must be InProgress or Completed."
            );

        if (requestedStatus.Value == SectionStatus.Completed && !hasSelectedSite)
            return Results.UnprocessableEntity(
                "Overseas sites section cannot be marked Completed until at least one site is selected."
            );

        overseasSites.SectionStatus = requestedStatus.Value;
        return null;
    }

    private static void ApplyPromotedFields(
        OverseasSiteModel site,
        PromoteOverseasSiteRequest request
    )
    {
        site.SiteName = request.SiteName;
        site.AddressLine1 = request.AddressLine1;
        site.AddressLine2 = request.AddressLine2;
        site.TownOrCity = request.TownOrCity;
        site.Country = request.Country;
        site.SiteAddress = string.Join(
            ", ",
            new[] { request.AddressLine1, request.TownOrCity, request.Country }.Where(s =>
                !string.IsNullOrWhiteSpace(s)
            )
        );
        site.Coordinates = request.Coordinates;
        site.ContactName = request.ContactName;
        site.ContactEmail = request.ContactEmail;
        site.ContactPhone = request.ContactPhone;
        site.OperationCodes = request.OperationCodes;
        site.Code1 = request.Code1;
        site.Code2 = request.Code2;
        site.Code3 = request.Code3;
        site.RepatriatedLoads = request.RepatriatedLoads;
        site.ConditionsOfExport = request.ConditionsOfExport;
        site.IsEu = CountryClassifications.IsEu(request.Country);
        site.IsOecd = CountryClassifications.IsOecd(request.Country);
    }

    private static void RestoreSnapshotFields(OverseasSiteModel site, OverseasSiteModel snapshot)
    {
        site.SiteName = snapshot.SiteName;
        site.SiteAddress = snapshot.SiteAddress;
        site.AddressLine1 = snapshot.AddressLine1;
        site.AddressLine2 = snapshot.AddressLine2;
        site.TownOrCity = snapshot.TownOrCity;
        site.Country = snapshot.Country;
        site.Coordinates = snapshot.Coordinates;
        site.ContactName = snapshot.ContactName;
        site.ContactEmail = snapshot.ContactEmail;
        site.ContactPhone = snapshot.ContactPhone;
        site.OperationCodes = snapshot.OperationCodes;
        site.Code1 = snapshot.Code1;
        site.Code2 = snapshot.Code2;
        site.Code3 = snapshot.Code3;
        site.RepatriatedLoads = snapshot.RepatriatedLoads;
        site.ConditionsOfExport = snapshot.ConditionsOfExport;
        site.IsEu = snapshot.IsEu;
        site.IsOecd = snapshot.IsOecd;
    }

    // Shared by PromoteOverseasSite and UpdateOverseasSite (RA-470): the undo-stack entry pushed
    // onto PreviousSites before either endpoint overwrites a site's fields - identical field set,
    // so it's captured once instead of duplicated across both handlers. Never nest a snapshot's
    // own PreviousSites.
    private static OverseasSiteModel SnapshotSiteFields(OverseasSiteModel site) =>
        new()
        {
            SiteId = site.SiteId,
            OrsId = site.OrsId,
            SiteName = site.SiteName,
            SiteAddress = site.SiteAddress,
            AddressLine1 = site.AddressLine1,
            AddressLine2 = site.AddressLine2,
            TownOrCity = site.TownOrCity,
            Country = site.Country,
            Coordinates = site.Coordinates,
            ContactName = site.ContactName,
            ContactEmail = site.ContactEmail,
            ContactPhone = site.ContactPhone,
            OperationCodes = site.OperationCodes,
            Code1 = site.Code1,
            Code2 = site.Code2,
            Code3 = site.Code3,
            RepatriatedLoads = site.RepatriatedLoads,
            ConditionsOfExport = site.ConditionsOfExport,
            IsEu = site.IsEu,
            IsOecd = site.IsOecd,
            Selected = site.Selected,
            IsNewSite = site.IsNewSite,
            RegisteredNowAccredited = site.RegisteredNowAccredited,
        };

    // Bundles UpdateOverseasSite's DI-service/framework parameters under one [AsParameters]
    // argument so the handler stays under Sonar's 7-parameter limit (S107) - same reasoning as
    // RecyclingOperationsServices above.
    private sealed record UpdateOverseasSiteServices(
        IAccreditationApplicationPersistence Persistence,
        IValidator<PromoteOverseasSiteRequest> Validator,
        IRecyclingOperationsAuditPersistence AuditPersistence,
        HttpContext HttpContext,
        CancellationToken CancellationToken
    );

    // RA-470: in-place "Change" edit of an already-selected overseas site - mirrors the frontend's
    // Add-To-Accreditation/promote wizard flow, reusing PromoteOverseasSiteRequest and its
    // validator exactly (same request/response contract the frontend agent is coding against
    // concurrently) rather than introducing a parallel DTO. Differs from PromoteOverseasSite in
    // three ways: (1) Selected/RegisteredNowAccredited are deliberately left untouched - the site
    // is already selected/accredited, this isn't a promotion; (2) OperationCodes go through the
    // same application/material-type-aware ValidateOperationCodesForSite check
    // PatchRecyclingOperations uses (gap 2), which Promote's validator alone doesn't cover; (3) an
    // orphaned-interim-site guard (gap 1), a BES-evidence invalidation on Country/
    // ConditionsOfExport change (gap 5), and an audit record of any operation-code change (gap 3,
    // matching PatchRecyclingOperations' regulator-driven audit trail).
    private static async Task<IResult> UpdateOverseasSite(
        string organisationId,
        string applicationId,
        int siteId,
        PromoteOverseasSiteRequest request,
        [AsParameters] UpdateOverseasSiteServices services
    )
    {
        var validation = await services.Validator.ValidateAsync(
            request,
            services.CancellationToken
        );
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await services.Persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.OverseasSites?.SectionStatus ?? SectionStatus.NotStarted
            )
        )
            return Results.Conflict(
                "Overseas sites section is not editable in the application's current status."
            );

        var site = application.OverseasSites?.Sites.FirstOrDefault(s => s.SiteId == siteId);
        if (site is null)
            return Results.NotFound();

        // Gap 2: the same application/material-type-aware check PatchRecyclingOperations enforces -
        // PromoteOverseasSiteRequestValidator alone doesn't cover it. The AC11 R12/R13-needs-
        // interim-site sub-check is deliberately NOT enforced here (enforceInterimSiteRequirement:
        // false) - the operator's Change wizard, like the existing Add/Promote entry points, PATCHes
        // the site before the separate interim-site sub-wizard attaches InterimSite; gap 1 below
        // still guards the opposite direction (an existing InterimSite left orphaned).
        if (
            ValidateOperationCodesForSite(
                application.MaterialType,
                request.OperationCodes,
                site,
                siteId,
                enforceInterimSiteRequirement: false
            ) is
            { } codesError
        )
            return codesError;

        // Gap 1: an interim site record can't be left dangling with no R12/R13 justifying it - the
        // inverse of PatchRecyclingOperations' "R12/R13 needs an interim site" check above.
        if (
            site.InterimSite is not null
            && !request.OperationCodes.Any(
                RecyclingOperationCodes.CodesRequiringAccompaniment.Contains
            )
        )
            return Results.BadRequest(
                $"Overseas site '{siteId}' has an associated interim site; at least one of R12/R13 must remain selected."
            );

        // Gap 5: BES evidence is tied to Country/ConditionsOfExport - either changing makes any
        // previously uploaded evidence and a Completed section status stale.
        var besEvidenceInvalidated =
            site.Country != request.Country
            || site.ConditionsOfExport != request.ConditionsOfExport;

        // Snapshot current fields (undo target) before overwriting - same as PromoteOverseasSite.
        site.PreviousSites.Add(SnapshotSiteFields(site));

        // Snapshotted before mutation, as an independent list - not aliased with the request's own
        // OperationCodes below (matches PatchRecyclingOperations' own before/after handling).
        var beforeCodes = site.OperationCodes.ToList();

        ApplyPromotedFields(site, request);
        // Deliberately NOT setting Selected/RegisteredNowAccredited here, unlike Promote - this is
        // an in-place edit of an already-selected/accredited site, not a promotion.

        if (besEvidenceInvalidated)
        {
            site.BesEvidence?.BesEvidenceUploads.Clear();
            if (application.BesEvidence?.SectionStatus == SectionStatus.Completed)
                application.BesEvidence.SectionStatus = SectionStatus.InProgress;
        }

        RecomputeOverseasSitesSectionStatus(application.OverseasSites!);
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await services.Persistence.UpdateAsync(application);
        if (updated is null)
            return Results.Problem("Failed to update overseas site.");

        // Gap 3: audit operator-driven operation-code changes the same way PatchRecyclingOperations
        // audits regulator-driven ones. Only written when the codes actually changed - unlike
        // PatchRecyclingOperations (which only ever changes codes), this endpoint edits many other
        // fields too, and a record with identical BeforeCodes/AfterCodes on every address/contact
        // edit would dilute an audit trail meant specifically for operation-code changes.
        //
        // Identity: unlike PatchRecyclingOperations (CaseManagement scheme, real per-caseworker
        // cdp_user_id/cdp_user_name claims from management-be), this endpoint runs under the
        // Frontend scheme - a plain service-to-service shared secret with no per-operator identity
        // attached (see FrontendAuthenticationHandler). "cdp_user_id"/"cdp_user_name" claims are
        // never present here, so reading them would silently write a blank actor on every record.
        // The one real signal available is the Frontend scheme's own NameIdentifier claim, which
        // at least identifies the record as operator-driven-via-frontend rather than falsely
        // implying a per-user lookup happened and came up empty. CdpUserName is left blank with
        // that same explanation baked into the value, so a reader of the audit trail sees the gap
        // rather than an unexplained blank. TRACKED FOLLOW-UP: real per-operator attribution needs
        // the frontend to forward the signed-in operator's own identity (mirroring how management-be
        // already forwards the caseworker's) and FrontendAuthenticationHandler to surface it as a
        // claim - out of scope for this endpoint alone.
        const string OperatorIdentityUnavailableReason =
            "Frontend scheme carries no per-operator identity - see UpdateOverseasSite audit comment";
        if (!beforeCodes.SequenceEqual(request.OperationCodes))
        {
            await services.AuditPersistence.RecordAsync(
                new RecyclingOperationsAuditRecord
                {
                    CdpUserId =
                        services.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                        ?? string.Empty,
                    CdpUserName = OperatorIdentityUnavailableReason,
                    OrganisationId = organisationId,
                    ApplicationId = applicationId,
                    SiteId = siteId,
                    BeforeCodes = beforeCodes,
                    AfterCodes = request.OperationCodes,
                },
                services.CancellationToken
            );
        }

        return Results.Ok(site);
    }

    private static async Task<IResult> PromoteOverseasSite(
        string organisationId,
        string applicationId,
        int siteId,
        PromoteOverseasSiteRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<PromoteOverseasSiteRequest> validator
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.OverseasSites?.SectionStatus ?? SectionStatus.NotStarted
            )
        )
            return Results.Conflict(
                "Overseas sites section is not editable in the application's current status."
            );

        var site = application.OverseasSites?.Sites.FirstOrDefault(s => s.SiteId == siteId);
        if (site is null)
            return Results.NotFound();

        // Snapshot current fields (undo target) before overwriting — never nest a snapshot's
        // own PreviousSites.
        site.PreviousSites.Add(SnapshotSiteFields(site));

        ApplyPromotedFields(site, request);
        site.Selected = true;
        site.RegisteredNowAccredited = true;

        RecomputeOverseasSitesSectionStatus(application.OverseasSites!);
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to promote overseas site.")
            : Results.Ok(site);
    }

    private static async Task<IResult> RevertOverseasSite(
        string organisationId,
        string applicationId,
        int siteId,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.OverseasSites?.SectionStatus ?? SectionStatus.NotStarted
            )
        )
            return Results.Conflict(
                "Overseas sites section is not editable in the application's current status."
            );

        var site = application.OverseasSites?.Sites.FirstOrDefault(s => s.SiteId == siteId);
        if (site is null)
            return Results.NotFound();

        if (!site.RegisteredNowAccredited || site.PreviousSites.Count == 0)
            return Results.Conflict(
                "This site has not been promoted from a registered site and cannot be reverted."
            );

        var snapshot = site.PreviousSites[^1];
        site.PreviousSites.RemoveAt(site.PreviousSites.Count - 1);

        RestoreSnapshotFields(site, snapshot);
        site.Selected = false;
        site.RegisteredNowAccredited = false;

        RecomputeOverseasSitesSectionStatus(application.OverseasSites!);
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to revert overseas site.")
            : Results.Ok(site);
    }

    private static async Task<IResult> AddInterimSite(
        string organisationId,
        string applicationId,
        int siteId,
        AddInterimSiteRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<AddInterimSiteRequest> validator,
        ICaseWorkingApiAdapter caseWorkingAdapter,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.OverseasSites?.SectionStatus ?? SectionStatus.NotStarted
            )
        )
            return Results.Conflict(
                "Overseas sites section is not editable in the application's current status."
            );

        var site = application.OverseasSites?.Sites.FirstOrDefault(s => s.SiteId == siteId);
        if (site is null)
            return Results.NotFound();

        if (site.InterimSite is not null)
            return Results.Conflict(
                $"An interim site already exists for overseas site '{siteId}'."
            );

        var nextSiteId = NextSiteId(application.OverseasSites!);

        var interimSite = new InterimSiteModel
        {
            SiteId = nextSiteId,
            SiteNumber = $"SN-{nextSiteId:D4}",
            Country = request.Country,
            SiteName = request.SiteName,
            AddressLine1 = request.AddressLine1,
            AddressLine2 = request.AddressLine2,
            TownOrCity = request.TownOrCity,
            StateOrRegion = request.StateOrRegion,
            Postcode = request.Postcode,
            ContactName = request.ContactName,
            ContactEmail = request.ContactEmail,
            ContactPhone = request.ContactPhone,
            IsNewSite = true,
        };

        site.InterimSite = interimSite;
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await persistence.UpdateAsync(application);
        if (updated is null)
            return Results.Problem("Failed to add interim site.");

        // Courtesy notification to ManagementBe — must never fail this response (RA-294 AC05 /
        // RA-297 AC04). Same guard/comment style as GetById (RA102-j7s).
        if (updated.CaseManagementWorkItemId is not null)
        {
            try
            {
                await caseWorkingAdapter.NotifySiteAddedAsync(
                    updated,
                    siteType: "interim",
                    orsId: site.OrsId ?? string.Empty,
                    siteNumber: interimSite.SiteNumber,
                    isNewSite: interimSite.IsNewSite,
                    cancellationToken: cancellationToken
                );
            }
            catch (Exception ex)
            {
                loggerFactory
                    .CreateLogger("AccreditationApplicationEndpoints")
                    .LogWarning(
                        ex,
                        "Failed to notify ManagementBe of new interim site for applicationId={ApplicationId}",
                        applicationId
                    );
            }
        }

        return Results.Created(string.Empty, interimSite);
    }

    private static async Task<IResult> AddBesEvidenceFile(
        string organisationId,
        string applicationId,
        int siteId,
        AddBesEvidenceFileRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<AddBesEvidenceFileRequest> validator,
        IPendingUploadService pendingUploadService
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        if (
            TryResolveScannedFile(
                request.FileUploadId,
                pendingUploadService,
                out var scannedFile
            ) is
            { } uploadError
        )
            return uploadError;

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.BesEvidence?.SectionStatus ?? SectionStatus.NotStarted
            )
        )
            return Results.Conflict(
                "BES evidence section is not editable in the application's current status."
            );

        var site = application.OverseasSites?.Sites.FirstOrDefault(s => s.SiteId == siteId);
        if (site is null)
            return Results.NotFound();

        var scanStatus = scannedFile.FileStatus == "complete" ? "Clean" : "Infected";

        site.BesEvidence ??= new BesEvidenceModel();
        site.BesEvidence.BesEvidenceUploads.Add(
            new BesEvidenceFileModel
            {
                FileId = scannedFile.FileId,
                Filename = scannedFile.Filename,
                ContentType = scannedFile.ContentType ?? scannedFile.DetectedContentType,
                ScanStatus = scanStatus,
                BesEvidenceValidFromDate = request.BesEvidenceValidFromDate,
                BesEvidenceExpiryDate = request.BesEvidenceExpiryDate,
                S3Key = scannedFile.S3Key,
                S3Bucket = scannedFile.S3Bucket,
            }
        );

        application.DateLastEdited = DateTime.UtcNow;
        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to add BES evidence file.")
            : Results.Created(string.Empty, site.BesEvidence);
    }

    private static async Task<IResult> PatchBesEvidence(
        string organisationId,
        string applicationId,
        int siteId,
        PatchBesEvidenceRequest request,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.BesEvidence?.SectionStatus ?? SectionStatus.NotStarted
            )
        )
            return Results.Conflict(
                "BES evidence section is not editable in the application's current status."
            );

        var site = application.OverseasSites?.Sites.FirstOrDefault(s => s.SiteId == siteId);
        if (site is null)
            return Results.NotFound();

        site.BesEvidence ??= new BesEvidenceModel();
        if (request.DoYouWantToUploadMoreEvidence.HasValue)
            site.BesEvidence.DoYouWantToUploadMoreEvidence = request
                .DoYouWantToUploadMoreEvidence
                .Value;

        application.DateLastEdited = DateTime.UtcNow;
        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update BES evidence.")
            : Results.Ok(updated);
    }

    private static async Task<IResult> DeleteBesEvidenceFile(
        string organisationId,
        string applicationId,
        int siteId,
        string fileId,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.BesEvidence?.SectionStatus ?? SectionStatus.NotStarted
            )
        )
            return Results.Conflict(
                "BES evidence section is not editable in the application's current status."
            );

        var site = application.OverseasSites?.Sites.FirstOrDefault(s => s.SiteId == siteId);
        if (site?.BesEvidence is null)
            return Results.NotFound();

        var removed = site.BesEvidence.BesEvidenceUploads.RemoveAll(f => f.FileId == fileId);
        if (removed == 0)
            return Results.NotFound();

        application.DateLastEdited = DateTime.UtcNow;
        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to delete BES evidence file.")
            : Results.Ok();
    }

    private static async Task<IResult> PatchBesEvidenceSection(
        string organisationId,
        string applicationId,
        PatchBesEvidenceSectionRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<PatchBesEvidenceSectionRequest> validator
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.BesEvidence?.SectionStatus ?? SectionStatus.NotStarted
            )
        )
            return Results.Conflict(
                "BES evidence section is not editable in the application's current status."
            );

        application.BesEvidence ??= new AccreditationApplicationBesEvidence();
        if (
            request.SectionStatus.HasValue
            && application.BesEvidence.SectionStatus != SectionStatus.Queried
        )
        {
            if (
                request.SectionStatus.Value == SectionStatus.Completed
                && !SectionStatusService.IsBesEvidenceComplete(application.OverseasSites)
            )
                return Results.UnprocessableEntity(
                    "BES evidence section cannot be marked Completed until evidence has been uploaded for every site that requires it."
                );
            application.BesEvidence.SectionStatus = request.SectionStatus.Value;
        }

        application.DateLastEdited = DateTime.UtcNow;
        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to update BES evidence section.")
            : Results.Ok(updated);
    }

    // RA-503: bundles Submit's DI-service/framework parameters under one [AsParameters]
    // argument so the handler stays under Sonar's 7-parameter limit (S107) - same reasoning
    // as RegulatoryNumberServices/RecyclingOperationsServices above. A positional record, not
    // bare `{ get; init; }` auto-properties, for the same S3459/S1144 reason given there.
    private sealed record SubmitServices(
        IAccreditationApplicationPersistence Persistence,
        ICaseWorkingApiAdapter CaseWorkingAdapter,
        IReExApiAdapter ReExAdapter,
        IValidator<SubmitRequest> Validator,
        CancellationToken CancellationToken
    );

    private static async Task<IResult> Submit(
        string organisationId,
        string applicationId,
        SubmitRequest request,
        [AsParameters] SubmitServices services
    )
    {
        var validation = await services.Validator.ValidateAsync(
            request,
            services.CancellationToken
        );
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await services.Persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        if (application.ApplicationStatus == ApplicationStatus.Submitted)
            return Results.Ok(
                new SubmitResponse { AccreditationReference = application.ApplicationReference }
            );

        if (application.ApplicationStatus != ApplicationStatus.Started)
            return Results.Conflict("Application must be in 'Started' status to submit.");

        // RA-470 gap 6: OverseasSites/BesEvidence are exporter-only sections (both are null for a
        // non-exporter application, see AccreditationApplicationModel), so these two checks are
        // gated on IsExporter - a non-exporter must not be blocked by sections it never has. This
        // is what gives UpdateOverseasSite's BES-evidence InProgress reset (gap 5) real teeth:
        // without it, that reset would only change a task-list label, never actually block submit.
        if (
            application.Prns.SectionStatus != SectionStatus.Completed
            || application.BusinessPlan.SectionStatus != SectionStatus.Completed
            || application.SamplingPlan.SectionStatus != SectionStatus.Completed
            || (
                application.IsExporter
                && application.OverseasSites?.SectionStatus != SectionStatus.Completed
            )
            || (
                application.IsExporter
                && application.BesEvidence?.SectionStatus != SectionStatus.Completed
            )
        )
        {
            return Results.BadRequest("All sections must be completed before submission.");
        }

        application.ApplicationStatus = ApplicationStatus.Submitted;
        application.DateSent = DateTime.UtcNow;
        application.DateLastEdited = DateTime.UtcNow;
        application.SubmittedBy = new SubmittedByModel
        {
            FullName = request.FullName,
            JobTitle = request.JobTitle,
            Email = request.Email,
        };

        // Version 1 for every section that exists on this application — only OverseasSites/
        // BesEvidence are exporter-specific, everything else applies regardless of IsExporter.
        var versionedAt = DateTime.UtcNow;
        AccreditationApplicationSections.SnapshotSection(
            application,
            OperatorSection.Prns,
            versionedAt
        );
        AccreditationApplicationSections.SnapshotSection(
            application,
            OperatorSection.BusinessPlan,
            versionedAt
        );
        AccreditationApplicationSections.SnapshotSection(
            application,
            OperatorSection.SamplingPlan,
            versionedAt
        );
        if (application.IsExporter)
        {
            AccreditationApplicationSections.SnapshotSection(
                application,
                OperatorSection.OverseasSites,
                versionedAt
            );
            AccreditationApplicationSections.SnapshotSection(
                application,
                OperatorSection.BesEvidence,
                versionedAt
            );
        }

        // RA-503: resolve ReEx's numeric OrgId (e.g. 500500) fresh, immediately before submission,
        // so the work-item payload carries the operator/regulator-safe organisation number rather
        // than the internal ObjectId in OrganisationId. Unlike ResolveOrgIdAsync's callers, Submit
        // has no caller-supplied fallback value to apply - a lookup failure leaves OrgId null
        // rather than blocking submission.
        application.OrgId = await ResolveOrgNumberFromReExAsync(
            organisationId,
            services.ReExAdapter,
            services.CancellationToken
        );

        // Call adapter before persisting: if adapter fails, DB is unchanged and the caller can retry safely.
        CaseWorkingSubmissionResult submissionResult;
        try
        {
            submissionResult = await services.CaseWorkingAdapter.SubmitApplicationAsync(
                application,
                services.CancellationToken
            );
        }
        catch (CaseWorkingApiTimeoutException)
        {
            // OJ FE's apiClient gives the whole submit POST a ~5s budget; without this, the
            // caller would instead see a generic 500 from ExceptionLoggingHandler only once
            // the "DefaultClient" HttpClient's own 15s Timeout (Program.cs) finally elapses,
            // long after OJ FE has already given up (RA-311). 504 + a distinct title lets OJ
            // FE's error handling distinguish "downstream timed out, maybe retry" from a
            // generic server error.
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "Case working service timed out",
                detail: "The case working service did not respond in time. Please try again."
            );
        }
        application.ApplicationReference = submissionResult.ApplicationReference;
        application.CaseManagementWorkItemId = submissionResult.WorkItemId;

        var updated = await services.Persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to submit accreditation application.")
            : Results.Ok(
                new SubmitResponse { AccreditationReference = updated.ApplicationReference }
            );
    }

    private static async Task<IResult> Resubmit(
        string organisationId,
        string applicationId,
        ResubmitRequest request,
        IAccreditationApplicationPersistence persistence,
        ICaseWorkingApiAdapter caseWorkingAdapter,
        CancellationToken cancellationToken
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        if (application.ApplicationStatus == ApplicationStatus.Updated)
            return Results.Ok(application);

        if (application.ApplicationStatus != ApplicationStatus.Queried)
            return Results.Conflict("Application must be in 'Queried' status to resubmit.");

        var sectionKeys = application.Query?.QueriedSectionKeys ?? [];
        var queriedSections = sectionKeys
            .Select(key =>
                AccreditationApplicationSections.TryMapCmKeyToSection(key, out var section)
                    ? section
                    : (OperatorSection?)null
            )
            .Where(section => section is not null)
            .Select(section => section!.Value)
            .Distinct()
            .ToList();

        var contactDetails = new QuerySubmitterContactDetails
        {
            FullName = request.FullName ?? string.Empty,
            Email = request.Email ?? string.Empty,
            Role = request.Role ?? string.Empty,
        };

        // Call adapter before persisting: if the call fails, leave ApplicationStatus at Queried
        // so the operator can retry — this matters more here than on the raise-side fire-and-
        // forget hook, since a failure after persisting Updated would lock the application with
        // CM never told.
        var result = await caseWorkingAdapter.ResumeFromQueryAsync(
            application,
            contactDetails,
            sectionKeys,
            cancellationToken
        );
        if (!result.IsSuccess)
            return Results.Problem(
                statusCode: 502,
                detail: "Failed to resume query with case management."
            );

        var versionedAt = DateTime.UtcNow;
        foreach (var section in queriedSections)
        {
            AccreditationApplicationSections.SnapshotSection(application, section, versionedAt);

            // Every queried section is still Queried here — the Patch* endpoints no longer clear
            // it as a side effect of an in-progress edit — so this branch fires for all of them,
            // touched or not, and ComputeCurrentStatus resolves each to its real value now that
            // the operator is done.
            if (
                AccreditationApplicationSections.GetSectionStatus(application, section)
                == SectionStatus.Queried
            )
                AccreditationApplicationSections.SetSectionStatus(
                    application,
                    section,
                    AccreditationApplicationSections.ComputeCurrentStatus(application, section)
                );
        }

        application.Query ??= new AccreditationApplicationQuery();
        application.Query.QuerySubmissions.Add(
            new QuerySubmission
            {
                QuerySubmissionTime = versionedAt,
                SectionKeys = sectionKeys,
                QuerySubmitterContactDetails = contactDetails,
            }
        );
        application.Query.QueriedSectionKeys = [];

        application.ApplicationStatus = ApplicationStatus.Updated;
        application.DateLastEdited = versionedAt;
        // Stamped alongside StatusChangedFromCaseManagement's own watermark (RA-368 §4.3) so a
        // single CaseManagementStatusUpdatedAt orders every CM-driven status write, resubmit or not.
        application.CaseManagementStatusUpdatedAt = versionedAt;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to resubmit accreditation application.")
            : Results.Ok(updated);
    }

    private static async Task<IResult> QueryFromCaseManagement(
        Guid workItemId,
        QueryFromCaseManagementRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<QueryFromCaseManagementRequest> validator,
        HttpContext httpContext,
        ILoggerFactory loggerFactory
    )
    {
        var logger = loggerFactory.CreateLogger("AccreditationApplicationEndpoints");
        // CM BE's push hook sends this on every request for cross-service tracing (RA-311).
        // Purely a diagnostic aid — absence must never fail the request.
        var correlationId = httpContext.Request.Headers.TryGetValue(
            "X-Correlation-Id",
            out var correlationValues
        )
            ? correlationValues.ToString()
            : null;

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "QueryFromCaseManagement request received for workItemId={WorkItemId} correlationId={CorrelationId}",
                workItemId,
                correlationId ?? "(absent)"
            );
        }

        var validation = await validator.ValidateAsync(request, httpContext.RequestAborted);
        if (!validation.IsValid)
        {
            logger.LogWarning(
                "QueryFromCaseManagement validation failed for workItemId={WorkItemId} correlationId={CorrelationId}: {Errors}",
                workItemId,
                correlationId ?? "(absent)",
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))
            );
            return Results.BadRequest(validation.Errors);
        }

        var application = await persistence.GetByCaseManagementWorkItemIdAsync(workItemId);
        if (application is null)
        {
            logger.LogWarning(
                "QueryFromCaseManagement: no application found for workItemId={WorkItemId} correlationId={CorrelationId}",
                workItemId,
                correlationId ?? "(absent)"
            );
            return Results.NotFound();
        }

        // A second query while one is already open is rejected rather than merged into the
        // existing QueriedSectionKeys (RA-311 §3) — the operator must resubmit the open query
        // before CM can raise another.
        if (application.ApplicationStatus == ApplicationStatus.Queried)
        {
            logger.LogWarning(
                "QueryFromCaseManagement: a query is already open for workItemId={WorkItemId} applicationId={ApplicationId} correlationId={CorrelationId}",
                workItemId,
                application.Id,
                correlationId ?? "(absent)"
            );
            return Results.Conflict("A query is already open for this application.");
        }

        if (
            application.ApplicationStatus
            is not (
                ApplicationStatus.Submitted
                or ApplicationStatus.DulyMade
                or ApplicationStatus.Updated
                or ApplicationStatus.AwaitingDecision
            )
        )
        {
            logger.LogWarning(
                "QueryFromCaseManagement: application status {Status} is not valid to raise a query for workItemId={WorkItemId} applicationId={ApplicationId} correlationId={CorrelationId}",
                application.ApplicationStatus,
                workItemId,
                application.Id,
                correlationId ?? "(absent)"
            );
            return Results.Conflict(
                "Application must be in 'Submitted', 'DulyMade', 'Updated' or 'AwaitingDecision' status to raise a query."
            );
        }

        if (
            !application.IsExporter
            && request.SectionKeys.Any(
                AccreditationApplicationSections.ExporterOnlyCmSectionKeys.Contains
            )
        )
        {
            logger.LogWarning(
                "QueryFromCaseManagement: exporter-only section keys rejected for non-exporter applicationId={ApplicationId} correlationId={CorrelationId}",
                application.Id,
                correlationId ?? "(absent)"
            );
            return Results.BadRequest(
                "BES evidence / overseas sites section keys are not valid for non-exporter applications."
            );
        }

        // Every key is already known-valid — the validator above rejects anything outside the
        // six-key set (AllCmSectionKeys), which is exactly what TryMapCmKeyToSection recognises.
        var sections = request
            .SectionKeys.Select(key =>
            {
                AccreditationApplicationSections.TryMapCmKeyToSection(key, out var section);
                return section;
            })
            .ToHashSet();

        foreach (var section in sections)
            AccreditationApplicationSections.SetSectionStatus(
                application,
                section,
                SectionStatus.Queried
            );

        // Note: QueryNote is user/CM-supplied free text and is intentionally never interpolated
        // into a log message (RA-311 security note) — only structured, server-known values are.
        application.Query ??= new AccreditationApplicationQuery();
        application.Query.QueryNote = request.QueryNote;
        application.Query.QueriedSectionKeys = request.SectionKeys;

        var queriedAt = DateTime.UtcNow;
        application.ApplicationStatus = ApplicationStatus.Queried;
        application.DateLastEdited = queriedAt;
        // Stamped alongside StatusChangedFromCaseManagement's own watermark (RA-368 §4.3) so a
        // single CaseManagementStatusUpdatedAt orders every CM-driven status write, query or not.
        application.CaseManagementStatusUpdatedAt = queriedAt;

        var updated = await persistence.UpdateAsync(application);
        if (updated is null)
        {
            logger.LogError(
                "QueryFromCaseManagement: failed to persist query for applicationId={ApplicationId} workItemId={WorkItemId} correlationId={CorrelationId}",
                application.Id,
                workItemId,
                correlationId ?? "(absent)"
            );
            return Results.Problem("Failed to record query from case management.");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "QueryFromCaseManagement succeeded for applicationId={ApplicationId} workItemId={WorkItemId} correlationId={CorrelationId}",
                updated.Id,
                workItemId,
                correlationId ?? "(absent)"
            );
        }
        return Results.Ok(updated);
    }

    private static async Task<IResult> AddFile(
        string organisationId,
        string applicationId,
        FileUploadRequest request,
        IAccreditationApplicationPersistence persistence,
        IValidator<FileUploadRequest> validator,
        IPendingUploadService pendingUploadService
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        if (
            TryResolveScannedFile(
                request.FileUploadId,
                pendingUploadService,
                out var scannedFile
            ) is
            { } uploadError
        )
            return uploadError;

        var contentType = scannedFile.ContentType ?? scannedFile.DetectedContentType;
        if (
            contentType is null
            || !FileUploadRequestValidator.PermittedContentTypes.Contains(contentType)
        )
            return Results.UnprocessableEntity("Content type is not permitted.");

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.SamplingPlan.SectionStatus
            )
        )
            return Results.Conflict(
                "Sampling plan section is not editable in the application's current status."
            );

        if (application.SamplingPlan.Files.Count >= 10)
            return Results.UnprocessableEntity("Maximum of 10 files permitted per application.");

        var file = new AccreditationApplicationFile
        {
            FileId = scannedFile.FileId,
            Filename = scannedFile.Filename,
            ContentType = contentType,
            UploadedByUserId = string.Empty, // TODO: populate from auth claims once auth PR lands
            ScanStatus =
                scannedFile.FileStatus == "complete"
                    ? FileScanStatus.Clean
                    : FileScanStatus.Infected,
            DocumentType = request.DocumentType,
            S3Key = scannedFile.S3Key,
            S3Bucket = scannedFile.S3Bucket,
        };

        application.SamplingPlan.Files.Add(file);
        if (application.SamplingPlan.SectionStatus != SectionStatus.Queried)
            application.SamplingPlan.SectionStatus = SectionStatusService.ComputeSamplingPlan(
                application.SamplingPlan
            );
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to add file.")
            : Results.Created(string.Empty, file);
    }

    private static async Task<IResult> DeleteFile(
        string organisationId,
        string applicationId,
        string fileId,
        IAccreditationApplicationPersistence persistence
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                application.SamplingPlan.SectionStatus
            )
        )
            return Results.Conflict(
                "Sampling plan section is not editable in the application's current status."
            );

        var removed = application.SamplingPlan.Files.RemoveAll(f => f.FileId == fileId);
        if (removed == 0)
            return Results.NotFound();

        if (application.SamplingPlan.SectionStatus != SectionStatus.Queried)
            application.SamplingPlan.SectionStatus = SectionStatusService.ComputeSamplingPlan(
                application.SamplingPlan
            );
        application.DateLastEdited = DateTime.UtcNow;

        if (application.ApplicationStatus == ApplicationStatus.Saved)
            application.ApplicationStatus = ApplicationStatus.Started;

        var updated = await persistence.UpdateAsync(application);
        return updated is null ? Results.Problem("Failed to delete file.") : Results.Ok();
    }

    // Raw CM state id -> the ApplicationStatus it projects onto in OJ (RA-368 §4.3). States with
    // no entry (anything CM adds in future) are a deliberate no-op for ApplicationStatus — the
    // push still updates CaseManagementStatusUpdatedAt for ordering purposes. "queried"/"withdrawn"
    // are never sent here: query keeps its own richer /query endpoint, and withdrawal is entirely
    // out of scope for this plan (§4.1, §4.5).
    private static ApplicationStatus? MapCaseManagementStateToApplicationStatus(string toStateId) =>
        toStateId switch
        {
            "submitted" => ApplicationStatus.Submitted,
            "duly-made" => ApplicationStatus.DulyMade,
            "assessment-in-progress" => ApplicationStatus.Updated,
            "updated" => ApplicationStatus.Updated,
            "awaiting-decision" => ApplicationStatus.AwaitingDecision,
            "approved" => ApplicationStatus.Approved,
            "rejected" => ApplicationStatus.Rejected,
            _ => null,
        };

    // Extracted from StatusChangedFromCaseManagement so the level check does not add to that
    // method's cognitive complexity (S3776), which is already at the limit.
    private static void LogOutOfOrderPushIgnored(
        ILogger logger,
        AccreditationApplicationModel application,
        Guid workItemId,
        DateTime occurredAt,
        DateTime lastUpdated,
        string? correlationId
    )
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StatusChangedFromCaseManagement: out-of-order or duplicate push ignored for applicationId={ApplicationId} workItemId={WorkItemId} occurredAt={OccurredAt} lastUpdated={LastUpdated} correlationId={CorrelationId}",
                application.Id,
                workItemId,
                occurredAt,
                lastUpdated,
                correlationId ?? "(absent)"
            );
        }
    }

    private static async Task<IResult> StatusChangedFromCaseManagement(
        Guid workItemId,
        StatusChangedFromCaseManagementRequest request,
        IAccreditationApplicationPersistence persistence,
        HttpContext httpContext,
        ILoggerFactory loggerFactory
    )
    {
        var logger = loggerFactory.CreateLogger("AccreditationApplicationEndpoints");
        // CM BE's push hook sends this on every request for cross-service tracing (RA-311/RA-368).
        // Purely a diagnostic aid — absence must never fail the request.
        var correlationId = httpContext.Request.Headers.TryGetValue(
            "X-Correlation-Id",
            out var correlationValues
        )
            ? correlationValues.ToString()
            : null;

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StatusChangedFromCaseManagement request received for workItemId={WorkItemId} toStateId={ToStateId} actionId={ActionId} correlationId={CorrelationId}",
                workItemId,
                request.ToStateId,
                request.ActionId,
                correlationId ?? "(absent)"
            );
        }

        var application = await persistence.GetByCaseManagementWorkItemIdAsync(workItemId);
        if (application is null)
        {
            logger.LogWarning(
                "StatusChangedFromCaseManagement: no application found for workItemId={WorkItemId} correlationId={CorrelationId}",
                workItemId,
                correlationId ?? "(absent)"
            );
            return Results.NotFound();
        }

        // Ordering guard, not a status-precedence table (RA-368 §4.3): a push whose OccurredAt is
        // not strictly after the last applied push is a duplicate or an out-of-order retry, so it
        // is accepted (200) but not applied.
        if (
            application.CaseManagementStatusUpdatedAt is { } lastUpdated
            && request.OccurredAt <= lastUpdated
        )
        {
            LogOutOfOrderPushIgnored(
                logger,
                application,
                workItemId,
                request.OccurredAt,
                lastUpdated,
                correlationId
            );
            return Results.Ok(application);
        }

        var mappedStatus = MapCaseManagementStateToApplicationStatus(request.ToStateId);

        // Terminal-status guard: once OJ has recorded a CM push as Approved, Rejected or
        // Withdrawn, no later mapped push may move the application again — a withdrawn
        // application re-opening as DulyMade (or an approved one flipping to Rejected) would
        // undo the very gates the terminal statuses exist to enforce. Unmapped pushes (anything
        // CM adds in future with no arm in the switch above) are exempt: they only update the
        // ordering watermark below, never ApplicationStatus.
        if (mappedStatus is not null && RejectIfTerminal(application) is not null)
        {
            logger.LogWarning(
                "StatusChangedFromCaseManagement: application status {Status} is terminal and cannot accept toStateId={ToStateId} for workItemId={WorkItemId} applicationId={ApplicationId} correlationId={CorrelationId}",
                application.ApplicationStatus,
                request.ToStateId,
                workItemId,
                application.Id,
                correlationId ?? "(absent)"
            );
            return Results.Conflict(
                "Application is already Approved, Rejected or Withdrawn and can no longer be updated."
            );
        }

        // Approve/reject legality: the old Approve/Reject endpoints carried this check but are
        // deleted (RA-368 §4.5) — nothing else in the codebase still enforces it, so it is
        // written fresh here (RA-368 §4.3).
        if (
            request.ToStateId is "approved" or "rejected"
            && application.ApplicationStatus
                is not (
                    ApplicationStatus.Submitted
                    or ApplicationStatus.Updated
                    or ApplicationStatus.DulyMade
                    or ApplicationStatus.AwaitingDecision
                )
        )
        {
            logger.LogWarning(
                "StatusChangedFromCaseManagement: application status {Status} is not valid for toStateId={ToStateId} for workItemId={WorkItemId} applicationId={ApplicationId} correlationId={CorrelationId}",
                application.ApplicationStatus,
                request.ToStateId,
                workItemId,
                application.Id,
                correlationId ?? "(absent)"
            );
            return Results.Conflict(
                "Application must be in 'Submitted', 'Updated', 'DulyMade' or 'AwaitingDecision' status to approve or reject."
            );
        }

        if (mappedStatus is { } newStatus)
            application.ApplicationStatus = newStatus;

        application.CaseManagementStatusUpdatedAt = request.OccurredAt;
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await persistence.UpdateAsync(application);
        if (updated is null)
        {
            logger.LogError(
                "StatusChangedFromCaseManagement: failed to persist status change for applicationId={ApplicationId} workItemId={WorkItemId} correlationId={CorrelationId}",
                application.Id,
                workItemId,
                correlationId ?? "(absent)"
            );
            return Results.Problem("Failed to record status change from case management.");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StatusChangedFromCaseManagement succeeded for applicationId={ApplicationId} workItemId={WorkItemId} toStateId={ToStateId} correlationId={CorrelationId}",
                updated.Id,
                workItemId,
                request.ToStateId,
                correlationId ?? "(absent)"
            );
        }
        return Results.Ok(updated);
    }

    private static async Task<IResult> Withdraw(
        string organisationId,
        string applicationId,
        WithdrawRequest request,
        IAccreditationApplicationPersistence persistence,
        ICaseWorkingApiAdapter caseWorkingAdapter,
        IValidator<WithdrawRequest> validator,
        CancellationToken cancellationToken
    )
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return Results.BadRequest(validation.Errors);

        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();

        if (application.ApplicationStatus == ApplicationStatus.Withdrawn)
            return Results.Ok(application);

        if (
            application.ApplicationStatus
            is not (
                ApplicationStatus.Submitted
                or ApplicationStatus.DulyMade
                or ApplicationStatus.Queried
                or ApplicationStatus.Updated
                or ApplicationStatus.AwaitingDecision
            )
        )
            return Results.Conflict("Only applications not yet decided can be withdrawn.");

        var contactDetails = new QuerySubmitterContactDetails
        {
            FullName = request.FullName ?? string.Empty,
            Email = request.Email ?? string.Empty,
            Role = string.Empty,
        };

        // Call adapter before persisting: if adapter fails, DB is unchanged and the caller can retry safely.
        var withdrawResult = await caseWorkingAdapter.WithdrawApplicationAsync(
            application,
            contactDetails,
            request.Reason,
            cancellationToken
        );
        if (!withdrawResult.IsSuccess)
            return Results.Problem(
                "Failed to withdraw accreditation application with the case management service."
            );

        if (application.ApplicationStatus == ApplicationStatus.Queried)
        {
            var queriedSections = (application.Query?.QueriedSectionKeys ?? [])
                .Select(key =>
                    AccreditationApplicationSections.TryMapCmKeyToSection(key, out var section)
                        ? section
                        : (OperatorSection?)null
                )
                .Where(section => section is not null)
                .Select(section => section!.Value)
                .Distinct();

            foreach (var section in queriedSections)
            {
                if (
                    AccreditationApplicationSections.GetSectionStatus(application, section)
                    == SectionStatus.Queried
                )
                    AccreditationApplicationSections.SetSectionStatus(
                        application,
                        section,
                        AccreditationApplicationSections.ComputeCurrentStatus(application, section)
                    );
            }

            application.Query ??= new AccreditationApplicationQuery();
            application.Query.QueriedSectionKeys = [];
        }

        application.ApplicationStatus = ApplicationStatus.Withdrawn;
        application.WithdrawalReason = request.Reason;
        application.DateLastEdited = DateTime.UtcNow;

        var updated = await persistence.UpdateAsync(application);
        return updated is null
            ? Results.Problem("Failed to withdraw accreditation application.")
            : Results.Ok(updated);
    }

    private static Task<IResult> InitiateUpload(
        string organisationId,
        string applicationId,
        InitiateUploadRequest request,
        IAccreditationApplicationPersistence persistence,
        ICdpUploaderService cdpUploaderService,
        IPendingUploadService pendingUploadService,
        IOptions<CdpUploaderConfig> cdpConfig,
        IOptions<AppConfig> appConfig,
        CancellationToken cancellationToken
    ) =>
        InitiateUploadInternal(
            organisationId,
            applicationId,
            request,
            persistence,
            cdpUploaderService,
            pendingUploadService,
            cdpConfig,
            appConfig,
            cancellationToken,
            cdpConfig.Value.SamplingPlanBucket,
            OperatorSection.SamplingPlan
        );

    private static Task<IResult> InitiateBesEvidenceUpload(
        string organisationId,
        string applicationId,
        InitiateUploadRequest request,
        IAccreditationApplicationPersistence persistence,
        ICdpUploaderService cdpUploaderService,
        IPendingUploadService pendingUploadService,
        IOptions<CdpUploaderConfig> cdpConfig,
        IOptions<AppConfig> appConfig,
        CancellationToken cancellationToken
    ) =>
        InitiateUploadInternal(
            organisationId,
            applicationId,
            request,
            persistence,
            cdpUploaderService,
            pendingUploadService,
            cdpConfig,
            appConfig,
            cancellationToken,
            cdpConfig.Value.BesEvidenceBucket,
            OperatorSection.BesEvidence
        );

    private static async Task<IResult> InitiateUploadInternal(
        string organisationId,
        string applicationId,
        InitiateUploadRequest request,
        IAccreditationApplicationPersistence persistence,
        ICdpUploaderService cdpUploaderService,
        IPendingUploadService pendingUploadService,
        IOptions<CdpUploaderConfig> cdpConfig,
        IOptions<AppConfig> appConfig,
        CancellationToken cancellationToken,
        string bucketPrefix,
        OperatorSection section
    )
    {
        var application = await persistence.GetByIdAsync(organisationId, applicationId);
        if (application is null)
            return Results.NotFound();
        if (RejectIfTerminal(application) is { } conflict)
            return conflict;

        if (
            !AccreditationApplicationSections.IsSectionEditable(
                application.ApplicationStatus,
                AccreditationApplicationSections.GetSectionStatus(application, section)
            )
        )
            return Results.Conflict("Section is not editable in the application's current status.");

        var fileUploadId = Guid.NewGuid().ToString();
        var baseUrl = appConfig.Value.BaseUrl.TrimEnd('/');
        var callbackUrl = $"{baseUrl}/api/v1/accreditation-applications/files/upload-completed";
        var statusUrl =
            $"{baseUrl}/api/v1/accreditation-applications/{organisationId}/{applicationId}/files/{fileUploadId}/status";

        var metadata = new Dictionary<string, string>(request.Metadata ?? [])
        {
            ["fileUploadId"] = fileUploadId,
        };

        var cdpRequest = new CdpInitiateRequest
        {
            Redirect = request.RedirectUrl,
            Callback = callbackUrl,
            S3Bucket = request.S3Bucket,
            S3Path = $"{bucketPrefix.Trim('/')}/{request.S3Path.TrimStart('/')}",
            MimeTypes = request.MimeTypes,
            MaxFileSize = request.MaxFileSize,
            Metadata = metadata,
        };

        var cdpResponse = await cdpUploaderService.InitiateAsync(cdpRequest, cancellationToken);
        pendingUploadService.Create(
            fileUploadId,
            cdpResponse.StatusUrl,
            cdpResponse.UploadId,
            cdpRequest.S3Bucket,
            cdpRequest.S3Path
        );

        return Results.Ok(
            new InitiateUploadResponse
            {
                FileUploadId = fileUploadId,
                UploadUrl = cdpResponse.UploadUrl,
                StatusUrl = statusUrl,
            }
        );
    }

    private static IResult UploadCompleted(
        CdpCallbackPayload payload,
        IPendingUploadService pendingUploadService
    )
    {
        var fileUploadId = payload.Metadata?.GetValueOrDefault("fileUploadId");
        if (string.IsNullOrWhiteSpace(fileUploadId))
            return Results.BadRequest("Missing fileUploadId in callback metadata.");

        var file = payload.Form?.File;
        if (file is null)
            return Results.BadRequest("Missing file in callback payload.");

        pendingUploadService.Complete(fileUploadId, file);
        return Results.Ok();
    }

    private static IResult GetUploadStatus(
        string fileUploadId,
        IPendingUploadService pendingUploadService
    )
    {
        var status = pendingUploadService.GetStatus(fileUploadId);
        return Results.Ok(status);
    }
}
