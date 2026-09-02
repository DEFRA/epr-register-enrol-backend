using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.ReEx.Dtos;
using EprRegisterEnrolBackend.Utils;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public class HttpReExApiAdapter(IReExClient reExClient, ILogger<HttpReExApiAdapter> logger)
    : IReExApiAdapter
{
    private static readonly Dictionary<string, PlannedTonnageBand> TonnageBandMap = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["up_to_500"] = PlannedTonnageBand.UpTo500,
        ["up_to_5000"] = PlannedTonnageBand.UpTo5000,
        ["up_to_10000"] = PlannedTonnageBand.UpTo10000,
        ["over_10000"] = PlannedTonnageBand.Over10000,
    };

    private static readonly Dictionary<string, GlassRecyclingProcess> GlassRecyclingProcessMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["glass_re_melt"] = GlassRecyclingProcess.Remelt,
            ["glass_other"] = GlassRecyclingProcess.Other,
        };

    private static readonly Dictionary<string, Action<ReExBusinessPlanDto, int>> BusinessPlanMap =
        new(StringComparer.Ordinal)
        {
            ["New reprocessing infrastructure and maintaining existing infrastructure"] = (
                dto,
                v
            ) => dto.NewInfrastructurePercent = v,
            ["Price support for buying packaging waste or selling recycled packaging waste"] = (
                dto,
                v
            ) => dto.PriceSupportPercent = v,
            ["Support for business collections"] = (dto, v) => dto.BusinessCollectionsPercent = v,
            ["Communications, including information campaigns"] = (dto, v) =>
                dto.CommunicationsPercent = v,
            ["Developing new markets for products made from recycled packaging waste"] = (dto, v) =>
                dto.NewMarketsPercent = v,
            ["Developing new uses for recycled packaging waste"] = (dto, v) =>
                dto.NewUsesPercent = v,
            ["Activities or investment not covered by the other categories"] = (dto, v) =>
                dto.OtherPercent = v,
        };

    public async Task<ReExResult<ReExAccreditationDto>> GetAccreditationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year
    )
    {
        var orgResult = await reExClient.GetOrganisationsAsync(organisationId);
        if (!orgResult.IsSuccess)
        {
            if (orgResult.IsNotFound)
                logger.LogWarning(
                    "ReEx organisation not found for organisationId={OrganisationId}",
                    organisationId
                );
            else
                logger.LogError(
                    "ReEx GetOrganisations failed for organisationId={OrganisationId}: {Error}",
                    organisationId,
                    orgResult.Error?.Message
                );
            return ReExResult<ReExAccreditationDto>.Fail(orgResult.Error!, orgResult.StatusCode);
        }

        var org = orgResult.Value!;

        // Locate the registration matching the requested registrationId
        var registration = org.Registrations.FirstOrDefault(r => r.Id == registrationId);
        if (registration is null)
        {
            logger.LogWarning(
                "No registration found for registrationId={RegistrationId} in org={OrganisationId}",
                registrationId,
                organisationId
            );
            return ReExResult<ReExAccreditationDto>.Fail(
                new ReExError(ReExErrorKind.NotFound, $"Registration {registrationId} not found"),
                404
            );
        }

        var isExporter = registration is ExporterRegistrationDto;

        // RA-526: registeredAddress is the proper UK registered-office address and is tried
        // first; address is a fallback. If neither has anything mappable, this organisation's
        // company details are unusable and the seed must abort rather than silently persist an
        // application with no company address at all.
        RegisteredAddressDto? companyAddressSource;
        bool isUkRegisteredAddress;
        if (HasMappableFields(org.CompanyDetails?.RegisteredAddress))
        {
            companyAddressSource = org.CompanyDetails!.RegisteredAddress;
            isUkRegisteredAddress = true;
        }
        else if (HasMappableFields(org.CompanyDetails?.Address))
        {
            companyAddressSource = org.CompanyDetails!.Address;
            isUkRegisteredAddress = false;
        }
        else
        {
            logger.LogError(
                "Could not map registeredAddress nor address from companyDetails for org={OrganisationId} reg={RegistrationId} registrationNumber={RegistrationNumber}",
                organisationId,
                registrationId,
                registration.RegistrationNumber
            );
            return ReExResult<ReExAccreditationDto>.Fail(
                new ReExError(
                    ReExErrorKind.ClientError,
                    "Organisation has no mappable registeredAddress or address in companyDetails."
                ),
                500
            );
        }

        if (isExporter && string.IsNullOrWhiteSpace(companyAddressSource?.Postcode))
        {
            // ROA: was a hard refusal (LogError + Fail 500) to avoid a silent regulator
            // fallback downstream. Every other postcode->nation resolution point in the
            // ecosystem (frontend nation-from-postcode.js, management-be NationResolver,
            // management-be ApplicationReferenceGenerator) already fails open to England
            // rather than blocking, so this refusal was the odd one out — it just moved the
            // failure here instead of preventing it. Source data quality for the missing
            // postcode is tracked separately; this adapter now matches the rest of the
            // ecosystem by continuing with a null postcode and logging a warning so the gap
            // stays observable. RA-526: this is a softer, distinct case from the hard-fail
            // above — an address is present, just without a postcode specifically.
            logger.LogWarning(
                "Exporter org={OrganisationId} has no registered-office postcode; continuing without it — downstream nation resolution will default to England",
                organisationId
            );
        }

        // RA-526: Regulator Nation is derived from THIS registration's own regulator, never the
        // organisation's (an org can hold registrations approved by different regulators) and
        // never from postcode. Missing/unrecognised codes default to England; an unrecognised
        // non-null code is logged so the gap stays observable, same precedent as the postcode
        // warning above.
        if (!RegulatorNationMapper.TryMap(registration.SubmittedToRegulator, out var nation))
        {
            logger.LogWarning(
                "Unrecognised regulator code {RegulatorCode} for org={OrganisationId} reg={RegistrationId}; defaulting to England",
                registration.SubmittedToRegulator,
                organisationId,
                registrationId
            );
        }

        string? siteAddress = registration is ReprocessorRegistrationDto reprocessor
            ? FormatAddress(reprocessor.Site?.Address)
            : null;

        // ReEx returns glassRecyclingProcess as an array of 0 or 1 elements; take the first
        // element defensively if ReEx ever returns more than one.
        GlassRecyclingProcess? glassRecyclingProcess = null;
        if (registration.GlassRecyclingProcess.Count > 0)
        {
            var rawGlassRecyclingProcess = registration.GlassRecyclingProcess[0];
            if (
                !GlassRecyclingProcessMap.TryGetValue(
                    rawGlassRecyclingProcess,
                    out var mappedGlassRecyclingProcess
                )
            )
                logger.LogWarning(
                    "Unrecognised GlassRecyclingProcess value: {Value}",
                    rawGlassRecyclingProcess
                );
            else
                glassRecyclingProcess = mappedGlassRecyclingProcess;
        }

        var companyRegisteredAddress = FormatAddress(companyAddressSource);

        var permitNumbers = registration
            .WasteManagementPermits.Select(p => p.PermitNumber)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();

        // Find the accreditation linked to this registration
        var accreditationId = registration.AccreditationId;
        var matches = org.Accreditations.Where(a => a.Id == accreditationId).ToList();

        if (matches.Count == 0)
        {
            logger.LogWarning(
                "No accreditation found for accreditationId={AccreditationId} (reg={RegistrationId} org={OrganisationId})",
                accreditationId,
                registrationId,
                organisationId
            );
            return ReExResult<ReExAccreditationDto>.Fail(
                new ReExError(ReExErrorKind.NotFound, $"Accreditation {accreditationId} not found"),
                404
            );
        }

        if (matches.Count > 1)
        {
            logger.LogError(
                "Duplicate accreditation IDs found for accreditationId={AccreditationId} (reg={RegistrationId} org={OrganisationId})",
                accreditationId,
                registrationId,
                organisationId
            );
            return ReExResult<ReExAccreditationDto>.Fail(
                new ReExError(
                    ReExErrorKind.ClientError,
                    "Duplicate accreditation IDs — data integrity violation"
                ),
                500
            );
        }

        var accreditation = matches[0];

        // Validate the year against the accreditation's validFrom date
        if (!DateOnly.TryParse(accreditation.ValidFrom, out var validFrom))
        {
            logger.LogError(
                "AccreditationId={AccreditationId}: validFrom is missing or unparseable ({Raw})",
                accreditationId,
                accreditation.ValidFrom
            );
            return ReExResult<ReExAccreditationDto>.Fail(
                new ReExError(
                    ReExErrorKind.ClientError,
                    "Accreditation validFrom is missing or unparseable"
                ),
                500
            );
        }

        if (validFrom.Year != year)
        {
            logger.LogWarning(
                "Accreditation year mismatch: requested={Year} validFrom={ValidFromYear} (accreditationId={AccreditationId})",
                year,
                validFrom.Year,
                accreditationId
            );
            return ReExResult<ReExAccreditationDto>.Fail(
                new ReExError(ReExErrorKind.NotFound, $"No accreditation for year {year}"),
                404
            );
        }

        // Map PRNs
        var prnIssuance = accreditation.PrnIssuance;

        PlannedTonnageBand? plannedTonnageBand = null;
        if (prnIssuance?.TonnageBand is { } rawBand)
        {
            if (!TonnageBandMap.TryGetValue(rawBand, out var mapped))
                logger.LogWarning("Unrecognised TonnageBand value: {Value}", rawBand);
            else
                plannedTonnageBand = mapped;
        }

        var authorisers =
            prnIssuance
                ?.Signatories.Select(s => new PrnsAuthoriser
                {
                    FullName = s.FullName ?? string.Empty,
                    Email = s.Email ?? string.Empty,
                })
                .ToList()
            ?? [];

        // Map business plan
        var businessPlan = new ReExBusinessPlanDto();
        foreach (var entry in prnIssuance?.IncomeBusinessPlan ?? [])
        {
            if (entry.UsageDescription is null || entry.PercentIncomeSpent is null)
                continue;

            if (BusinessPlanMap.TryGetValue(entry.UsageDescription, out var setter))
                setter(businessPlan, entry.PercentIncomeSpent.Value);
            else
                logger.LogWarning(
                    "Unrecognised IncomeBusinessPlan usageDescription: {Desc}",
                    entry.UsageDescription
                );
        }

        // Fetch overseas sites for exporters
        List<OverseasSiteModel> overseasSites = [];
        if (isExporter)
        {
            var sitesResult = await reExClient.GetOverseasSiteAsync(
                organisationId,
                registrationId,
                accreditation.Id!,
                CancellationToken.None
            );

            if (!sitesResult.IsSuccess)
            {
                logger.LogError(
                    "Overseas sites call failed for accreditationId={AccreditationId}: {Error}",
                    accreditation.Id,
                    sitesResult.Error?.Message
                );
                return ReExResult<ReExAccreditationDto>.Fail(
                    sitesResult.Error!,
                    sitesResult.StatusCode
                );
            }

            overseasSites = sitesResult
                .Value!.Select(kvp => MapOverseasSite(kvp.Key, kvp.Value))
                .ToList();
        }

        return ReExResult<ReExAccreditationDto>.Success(
            new ReExAccreditationDto
            {
                AccreditationId = accreditation.Id,
                OrganisationId = organisationId,
                MaterialType = materialType,
                Year = year,
                OrganisationName = org.CompanyDetails?.Name,
                RegistrationReference = registration.RegistrationNumber,
                SiteAddress = siteAddress,
                IsExporter = isExporter,
                Nation = nation,
                CompanyRegisterAddressPostcode = companyAddressSource?.Postcode,
                CompanyRegisteredAddress = companyRegisteredAddress,
                IsUkRegisteredAddress = isUkRegisteredAddress,
                CompaniesHouseNumber = org.CompanyDetails?.CompaniesHouseNumber,
                PermitNumbers = permitNumbers,
                WasteProcessingType = isExporter ? "exporter" : "reprocessor",
                GlassRecyclingProcess = glassRecyclingProcess,
                OverseasSites = overseasSites,
                Prns = new ReExPrnsDto
                {
                    PlannedTonnageBand = plannedTonnageBand,
                    Authorisers = authorisers,
                },
                BusinessPlan = businessPlan,
                SubmitterContactDetails = org.SubmitterContactDetails is { } submitterContact
                    ? new SubmitterContactDetailsDto
                    {
                        FullName = submitterContact.FullName,
                        Email = submitterContact.Email,
                        Phone = submitterContact.Phone,
                        JobTitle = submitterContact.JobTitle,
                    }
                    : null,
            },
            200
        );
    }

    public async Task<ReExResult<LinkedDefraOrganisationResult>> GetLinkedDefraOrganisationAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    )
    {
        var orgResult = await reExClient.GetOrganisationsAsync(organisationId, cancellationToken);
        if (!orgResult.IsSuccess)
        {
            if (orgResult.IsNotFound)
                logger.LogWarning(
                    "ReEx organisation not found for organisationId={OrganisationId}",
                    organisationId
                );
            else
                logger.LogError(
                    "ReEx GetOrganisations failed for organisationId={OrganisationId}: {Error}",
                    organisationId,
                    orgResult.Error?.Message
                );
            return ReExResult<LinkedDefraOrganisationResult>.Fail(
                orgResult.Error!,
                orgResult.StatusCode
            );
        }

        var linkedOrgId = orgResult.Value!.LinkedDefraOrganisation?.OrgId;
        if (linkedOrgId is null)
            logger.LogWarning(
                "ReEx organisation {OrganisationId} has no linkedDefraOrganisation.orgId",
                organisationId
            );

        return ReExResult<LinkedDefraOrganisationResult>.Success(
            new LinkedDefraOrganisationResult
            {
                OrganisationId = organisationId,
                LinkedDefraOrganisationId = linkedOrgId,
            },
            200
        );
    }

    public async Task<ReExResult<int?>> GetOrganisationNumberAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    )
    {
        var orgResult = await reExClient.GetOrganisationsAsync(organisationId, cancellationToken);
        if (!orgResult.IsSuccess)
        {
            if (orgResult.IsNotFound)
                logger.LogWarning(
                    "ReEx organisation not found for organisationId={OrganisationId}",
                    organisationId
                );
            else
                logger.LogError(
                    "ReEx GetOrganisations failed for organisationId={OrganisationId}: {Error}",
                    organisationId,
                    orgResult.Error?.Message
                );
            return ReExResult<int?>.Fail(orgResult.Error!, orgResult.StatusCode);
        }

        var orgNumber = orgResult.Value!.OrgId;
        if (orgNumber is null)
            logger.LogWarning(
                "ReEx organisation {OrganisationId} has no numeric orgId; a regulatory number "
                    + "cannot be generated from it",
                organisationId
            );

        return ReExResult<int?>.Success(orgNumber, 200);
    }

    private static OverseasSiteModel MapOverseasSite(string key, OverseasSiteDto dto) =>
        new()
        {
            SiteId = int.TryParse(key, out var id) ? id : 0,
            // RA-507: key is the existing ORS id ReEx already assigned this site (ReEx's own
            // overseas-sites endpoint keys its response by "the three-digit ORS id"), not just a
            // local site index. Populating it here lets OrsIdGenerator see ReEx-assigned ids when
            // computing the next one for an operator-added site, avoiding duplicate ORS ids.
            OrsId = key,
            SiteName = dto.Name ?? string.Empty,
            SiteAddress = dto.Address is { } addr
                ? $"{addr.Line1}, {addr.TownOrCity}".Trim(',', ' ')
                : null,
            Country = dto.Country,
            IsEu = CountryClassifications.IsEu(dto.Country),
            IsOecd = CountryClassifications.IsOecd(dto.Country),
            Selected = false,
            IsNewSite = false,
        };

    private static string? FormatAddress(SiteAddressDto? addr) =>
        addr is null
            ? null
            : string.Join(
                ", ",
                new[] { addr.Line1, addr.Town, addr.Postcode }.Where(s =>
                    !string.IsNullOrWhiteSpace(s)
                )
            );

    private static string? FormatAddress(RegisteredAddressDto? addr) =>
        addr is null
            ? null
            : string.Join(
                ", ",
                new[] { addr.Line1, addr.Line2, addr.Town, addr.County, addr.Postcode }.Where(s =>
                    !string.IsNullOrWhiteSpace(s)
                )
            );

    // RA-526: "mappable" means at least one field is non-null/non-empty - an address object
    // with every field null or "" carries nothing worth mapping, and is treated the same as no
    // address at all when deciding whether to fall back / abort.
    private static bool HasMappableFields(RegisteredAddressDto? addr) =>
        addr is not null
        && new[]
        {
            addr.Line1,
            addr.Line2,
            addr.Town,
            addr.County,
            addr.Country,
            addr.Postcode,
        }.Any(s => !string.IsNullOrWhiteSpace(s));
}
