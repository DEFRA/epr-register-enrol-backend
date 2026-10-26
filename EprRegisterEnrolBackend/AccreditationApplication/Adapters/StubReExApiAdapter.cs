using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Utils;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public class StubReExApiAdapter(
    FakeOrganisationPersistence fakeOrgs,
    ILogger<StubReExApiAdapter> logger
) : IReExApiAdapter
{
    private static readonly (string Country, bool IsEu, bool IsOecd)[] StubSiteData =
    [
        ("Germany", true, true),
        ("France", true, true),
        ("Japan", false, true),
        ("Vietnam", false, false),
    ];

    // Mirrors HttpReExApiAdapter.GlassRecyclingProcessMap — the stub reads the
    // same wire-value string from FakeOrganisationPersistence's seed data that
    // the real ReEx API would return, so local dev/e2e exercises the same
    // mapping the production adapter does.
    private static readonly Dictionary<string, GlassRecyclingProcess> GlassRecyclingProcessMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["glass_re_melt"] = GlassRecyclingProcess.Remelt,
            ["glass_other"] = GlassRecyclingProcess.Other,
        };

    // RA-000: E2E test organisations ("e2e-" ids) must never hand GOV.UK Notify a deliverable
    // address, so every contact email the stub invents for them is Notify's simulated inbox.
    internal const string TestOrganisationEmail = "simulate-delivered@notifications.service.gov.uk";

    // RA-000: 900000-999999. Six digits, because the org number fills the {OrgId:D6} segment of
    // regulatory numbers; kept above the five-digit FakeOrganisationPersistence fixture ids.
    private const int TestOrganisationNumberBase = 900_000;
    private const int TestOrganisationNumberRange = 100_000;

    // RA-690: the 6-digit OrgId and 4-digit sequence segments of the derived registration
    // reference.
    private const int ReferenceOrganisationNumberRange = 1_000_000;
    private const uint RegistrationSequenceRange = 10_000;

    public async Task<ReExResult<ReExAccreditationDto>> GetAccreditationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year
    )
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubReExApiAdapter.GetAccreditationAsync called for org={OrganisationId} reg={RegistrationId} material={MaterialType} year={Year}",
                organisationId,
                registrationId,
                materialType,
                year
            );
        }

        string? organisationName = null;
        string? siteAddress = null;
        string? companyRegisterAddressPostcode = null;
        string? companyRegisteredAddress = null;
        string? companiesHouseNumber = null;
        List<string> permitNumbers = [];
        string? wasteProcessingType = null;
        var isExporter = false;
        GlassRecyclingProcess? glassRecyclingProcess = null;
        List<OverseasSiteModel> overseasSites = [];
        string? submittedToRegulator = null;

        if (int.TryParse(organisationId, out var orgIdInt))
        {
            var org = await fakeOrgs.GetByOrgIdAsync(orgIdInt);
            submittedToRegulator = org?.SubmittedToRegulator;
            organisationName = org?.CompanyDetails?.Name;
            companyRegisterAddressPostcode = org?.CompanyDetails?.RegisteredAddress?.Postcode;
            companiesHouseNumber = org?.CompanyDetails?.CompaniesHouseNumber;
            companyRegisteredAddress = org?.CompanyDetails?.RegisteredAddress is { } regAddr
                ? string.Join(
                    ", ",
                    new[]
                    {
                        regAddr.Line1,
                        regAddr.Line2,
                        regAddr.Town,
                        regAddr.County,
                        regAddr.Postcode,
                    }.Where(s => !string.IsNullOrWhiteSpace(s))
                )
                : null;

            var registration = org?.Registrations?.FirstOrDefault(r =>
                r.Id.ToString() == registrationId
            );
            wasteProcessingType = registration?.WasteProcessingType;
            if (
                registration?.GlassRecyclingProcess is { } rawGlassRecyclingProcess
                && GlassRecyclingProcessMap.TryGetValue(
                    rawGlassRecyclingProcess,
                    out var mappedGlassRecyclingProcess
                )
            )
                glassRecyclingProcess = mappedGlassRecyclingProcess;
            isExporter =
                wasteProcessingType?.Equals("exporter", StringComparison.OrdinalIgnoreCase) == true;
            // Mirrors HttpReExApiAdapter: only reprocessors have a UK processing
            // site, so exporters always get a null SiteAddress from the real API.
            siteAddress =
                isExporter ? null
                : registration?.SiteAddress is { } addr
                    ? $"{addr.Line1}, {addr.Town}, {addr.Postcode}"
                : "1 Stub Lane, Stubton, ST1 1AB";

            permitNumbers = (registration?.WasteManagementPermits ?? [])
                .Select(p => p.PermitNumber)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .ToList();

            if (registration?.OverseasSites is { Count: > 0 } siteIds)
            {
                overseasSites = siteIds
                    .Select(
                        (id, i) =>
                        {
                            var (country, isEu, isOecd) =
                                i < StubSiteData.Length
                                    ? StubSiteData[i]
                                    : ("Unknown", false, false);
                            return new OverseasSiteModel
                            {
                                SiteId = int.TryParse(id, out var parsed) ? parsed : 900001 + i,
                                // RA-507: NOT `OrsId = id` -- unlike the real ReEx API's key
                                // (always a genuine 3-digit ORS id, per epr-backend's own
                                // contract), FakeOrganisationPersistence's OverseasSites entries
                                // are 6-digit SiteId placeholders (e.g. "900001") with no such
                                // guarantee. Feeding one straight into OrsIdGenerator as an
                                // "existing" id made it compute a next-id past its 999 cap and
                                // fail every AddOverseasSite call for these seeded orgs. Synthesize
                                // a plausible, correctly-shaped 3-digit id from position instead.
                                OrsId = (i + 1).ToString("D3"),
                                SiteName = $"Overseas Site {i + 1} ({country})",
                                SiteAddress = $"Address {id}",
                                Country = country,
                                IsEu = isEu,
                                IsOecd = isOecd,
                                IsNewSite = false,
                            };
                        }
                    )
                    .ToList();
            }
        }

        // RA-553: same source as GetNationAsync below - the fixture's SubmittedToRegulator
        // code, mirroring HttpReExApiAdapter.GetAccreditationAsync deriving Nation from the
        // real ReEx registration's regulator. Without this, seeded stub applications always
        // defaulted to Nation.England regardless of the org's actual regulator/postcode.
        RegulatorNationMapper.TryMap(submittedToRegulator, out var nation);

        // RA-000: E2E test organisations get a recognisable, per-org name so each test can find
        // its own items in Case Management. Nation is England via the mapper default above (no
        // fixture org, so no regulator code).
        var isTestOrganisation = TestOrganisationsConfig.IsTestOrganisation(organisationId);
        if (isTestOrganisation)
            organisationName = $"E2E Organisation {organisationId}";

        var fixture = new ReExAccreditationDto
        {
            AccreditationId = $"reex-acc-{organisationId}-{materialType}-{year}",
            OrganisationId = organisationId,
            MaterialType = materialType,
            Year = year,
            OrganisationName = organisationName ?? "Stub Reprocessing Ltd",
            RegistrationReference = DerivedRegistrationReference(
                organisationId,
                registrationId,
                materialType,
                glassRecyclingProcess,
                year,
                nation,
                isExporter
            ),
            SiteAddress = siteAddress,
            IsExporter = isExporter,
            Nation = nation,
            CompanyRegisterAddressPostcode = companyRegisterAddressPostcode ?? "ST1 1AB",
            CompanyRegisteredAddress =
                companyRegisteredAddress ?? "1 Stub Registered Office, Stubton, ST1 1AB",
            CompaniesHouseNumber = companiesHouseNumber ?? "00000001",
            PermitNumbers = permitNumbers,
            WasteProcessingType = wasteProcessingType ?? (isExporter ? "exporter" : "reprocessor"),
            GlassRecyclingProcess = glassRecyclingProcess,
            OverseasSites = overseasSites,
            Prns = new ReExPrnsDto
            {
                PlannedTonnageBand = PlannedTonnageBand.UpTo5000,
                Authorisers =
                [
                    new PrnsAuthoriser
                    {
                        FullName = "Stub Authoriser",
                        Email = isTestOrganisation ? TestOrganisationEmail : "stub@example.com",
                    },
                ],
            },
            BusinessPlan = new ReExBusinessPlanDto
            {
                NewInfrastructurePercent = 20,
                PriceSupportPercent = 20,
                BusinessCollectionsPercent = 15,
                CommunicationsPercent = 15,
                NewMarketsPercent = 10,
                NewUsesPercent = 10,
                OtherPercent = 10,
            },
            SubmitterContactDetails = new SubmitterContactDetailsDto
            {
                FullName = "Stub Submitter",
                Email = isTestOrganisation
                    ? TestOrganisationEmail
                    : "stub.submitter@example.com",
                Phone = "01234 567890",
                JobTitle = "Stub Job Title",
            },
        };

        return ReExResult<ReExAccreditationDto>.Success(fixture, 200);
    }

    public Task<ReExResult<LinkedDefraOrganisationResult>> GetLinkedDefraOrganisationAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    )
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubReExApiAdapter.GetLinkedDefraOrganisationAsync called for org={OrganisationId}",
                organisationId
            );
        }

        // Stub: the linked Defra organisation id echoes the ReEx org id so local
        // dev and integration runs are self-consistent.
        return Task.FromResult(
            ReExResult<LinkedDefraOrganisationResult>.Success(
                new LinkedDefraOrganisationResult
                {
                    OrganisationId = organisationId,
                    LinkedDefraOrganisationId = organisationId,
                },
                200
            )
        );
    }

    public Task<ReExResult<int?>> GetOrganisationNumberAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    )
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubReExApiAdapter.GetOrganisationNumberAsync called for org={OrganisationId}",
                organisationId
            );
        }

        // Stub: a numeric org id passes straight through so a locally-seeded
        // numeric organisation behaves exactly as the real ReEx would. Anything
        // else (the UUID a real ReEx organisation id actually is) reports "no
        // orgId recorded" rather than inventing one - the caller then falls back
        // to its own supplied value, which is what every existing local/dev
        // fixture relies on.
        // RA-000: an E2E test organisation gets a stable org number derived from its id.
        // management-be sends OrgId null for any non-numeric organisationId and relies on this
        // lookup, so without one, approving a test organisation's application would fail at
        // accreditation-number generation ("Nation, OrgId and Year are required").
        if (TestOrganisationsConfig.IsTestOrganisation(organisationId))
            return Task.FromResult(
                ReExResult<int?>.Success(TestOrganisationNumber(organisationId), 200)
            );

        var orgNumber = int.TryParse(
            organisationId,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed
        )
            ? parsed
            : (int?)null;

        return Task.FromResult(ReExResult<int?>.Success(orgNumber, 200));
    }

    public async Task<ReExResult<Nation>> GetNationAsync(
        string organisationId,
        string registrationId,
        CancellationToken cancellationToken = default
    )
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubReExApiAdapter.GetNationAsync called for org={OrganisationId} reg={RegistrationId}",
                organisationId,
                registrationId
            );
        }

        // RA-553: resolve nation from the fixture's SubmittedToRegulator code, same as
        // HttpReExApiAdapter does from the real ReEx API's regulator field. Falls back to
        // England when the org can't be found or has no regulator code set - the same
        // default RegulatorNationMapper itself applies.
        string? submittedToRegulator = null;
        if (int.TryParse(organisationId, out var orgIdInt))
        {
            var org = await fakeOrgs.GetByOrgIdAsync(orgIdInt);
            submittedToRegulator = org?.SubmittedToRegulator;
        }

        RegulatorNationMapper.TryMap(submittedToRegulator, out var nation);
        return ReExResult<Nation>.Success(nation, 200);
    }

    // RA-690: a valid 17-character registration reference, derived so it is the same every time
    // a given registration is seeded and differs between registrations - approval derives the
    // accreditation reference from it, so a malformed or shared one would fail approval (invalid
    // source, or a clash with another application's reference on repeated e2e runs). Uniqueness
    // is probabilistic: the 4-digit sequence is a hash, so two registrations of one organisation
    // collide about 1 time in 10,000 - fine for stub data, where each e2e organisation also has
    // its own org number.
    private static string DerivedRegistrationReference(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        GlassRecyclingProcess? glassRecyclingProcess,
        int year,
        Nation nation,
        bool isExporter
    )
    {
        var agencyLetter = RegulatoryReferenceCodes.AgencyLetterFor(nation);
        var processTypeLetter = RegulatoryReferenceCodes.ProcessTypeLetterFor(isExporter);
        // Real ReEx always reports a glass registration's recycling process; when the stub's
        // fixture has none (or an unrecognised one), "other" is the honest fallback.
        var materialCode = RegulatoryReferenceCodes.MaterialCodeFor(
            materialType,
            materialType == MaterialType.Glass
                ? glassRecyclingProcess ?? GlassRecyclingProcess.Other
                : glassRecyclingProcess
        );
        var sequence = Fnv1a(registrationId) % RegistrationSequenceRange;

        return $"R{year % 100:D2}{agencyLetter}{processTypeLetter}"
            + $"{ReferenceOrganisationNumber(organisationId):D6}{sequence:D4}{materialCode}";
    }

    // The stub's own organisation number when it has one that fits the 6-digit OrgId segment
    // (a test organisation's 900000-999999 number, or a numeric fixture id); otherwise a stable
    // hash of the id, e.g. for the UUID a real ReEx organisation id is.
    private static int ReferenceOrganisationNumber(string organisationId)
    {
        if (TestOrganisationsConfig.IsTestOrganisation(organisationId))
            return TestOrganisationNumber(organisationId);

        if (
            int.TryParse(
                organisationId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var orgNumber
            )
            && orgNumber < ReferenceOrganisationNumberRange
        )
            return orgNumber;

        return (int)(Fnv1a(organisationId) % ReferenceOrganisationNumberRange);
    }

    internal static int TestOrganisationNumber(string organisationId) =>
        TestOrganisationNumberBase + (int)(Fnv1a(organisationId) % TestOrganisationNumberRange);

    // FNV-1a: deterministic across processes and restarts (unlike string.GetHashCode), so a
    // test organisation keeps the same number, and a registration the same reference, for its
    // whole life.
    private static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
            hash = (hash ^ b) * 16777619u;
        return hash;
    }
}
