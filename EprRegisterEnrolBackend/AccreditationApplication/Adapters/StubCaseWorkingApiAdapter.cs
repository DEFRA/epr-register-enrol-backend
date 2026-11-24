using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationChanges;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

// Stub implementation — swapped for a real HTTP adapter once the Case Working Service contract is defined.
public class StubCaseWorkingApiAdapter(ILogger<StubCaseWorkingApiAdapter> logger)
    : ICaseWorkingApiAdapter
{
    // RA-492: mirrors ManagementBe's in-year change reference, IYC-{code}-{yyyy}-{seq:D5}, where
    // the code names the change type. Unknown change types are rejected, as ManagementBe rejects
    // a work-item type it has not registered.
    // ManagementBe is the source of truth for these codes: each work-item type declares its own
    // InYearChangeCode (documented in its docs/work-items.md, "Application reference"). Adding
    // a change type means adding its code there and here.
    private static readonly Dictionary<string, string> s_changeTypeCodes = new()
    {
        [AccreditationChangeRequest.AddOverseasSiteChangeType] = "ORS",
    };

    // Stub only: ManagementBe owns the real sequences, one per change code per year for the
    // in-year change references and one per year for the submission references. These in-process
    // counters are thread-safe but restart at 1 with the app and are not shared between
    // instances. One adapter is registered as a singleton.
    private readonly ConcurrentDictionary<(string Code, int Year), long> _changeSequences = new();
    private readonly ConcurrentDictionary<int, long> _submissionSequences = new();

    // The in-year change sequence segment is always five digits (RA-492). As ManagementBe
    // (RA-739), the counter keeps increasing from 1 and the reference cycles through 00001 to
    // 99999, so 99999 is followed by 00001 and 00000 is never shown.
    private const long MaxChangeSequence = 99_999;

    // ManagementBe accepts only a whole four-digit accreditation year.
    private const int MinChangeYear = 1000;
    private const int MaxChangeYear = 9999;

    // RA-318: mirrors ManagementBe's ApplicationReferenceGenerator format so local/dev
    // testing sees realistic references — there is no real ManagementBe to call here, so
    // the stub must fabricate one itself rather than leaving it to a downstream service.
    private static readonly HashSet<string> s_scotlandPrefixes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "AB",
        "DD",
        "DG",
        "EH",
        "FK",
        "G",
        "HS",
        "IV",
        "KA",
        "KW",
        "KY",
        "ML",
        "PA",
        "PH",
        "TD",
        "ZE",
    };

    private static readonly HashSet<string> s_walesPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CF",
        "CH",
        "LD",
        "LL",
        "NP",
        "SA",
        "SY",
    };

    private const string NiPrefix = "BT";

    public Task<CaseWorkingSubmissionResult> SubmitApplicationAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    )
    {
        var applicationReference = GenerateReference(application);
        var workItemId = Guid.NewGuid();

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubCaseWorkingApiAdapter.SubmitApplicationAsync called for applicationId={ApplicationId} generatedRef={ApplicationReference} workItemId={WorkItemId} org={OrganisationId}",
                application.Id,
                applicationReference,
                workItemId,
                application.OrganisationId
            );
        }

        return Task.FromResult(new CaseWorkingSubmissionResult(applicationReference, workItemId));
    }

    // RA-739: as ManagementBe, one work item per site, each with its own in-year change reference,
    // and one submission reference they share.
    public Task<ChangeRequestSubmissionResult> SubmitChangeRequestAsync(
        AccreditationChangeRequest changeRequest,
        AccreditationApplicationModel accreditation,
        CancellationToken cancellationToken = default
    )
    {
        var code = ChangeCodeFor(changeRequest);
        var year = changeRequest.Year;
        var submissionReference = FormatSubmissionReference(
            year,
            _submissionSequences.AddOrUpdate(year, 1, (_, current) => current + 1)
        );
        var sites = changeRequest
            .Sites.Select(site => new SiteWorkItemResult(
                site.SiteId,
                FormatChangeReference(
                    code,
                    year,
                    _changeSequences.AddOrUpdate((code, year), 1, (_, current) => current + 1)
                ),
                Guid.NewGuid()
            ))
            .ToList();

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubCaseWorkingApiAdapter.SubmitChangeRequestAsync called for requestId={RequestId} submissionReference={SubmissionReference} references={References} org={OrganisationId}",
                changeRequest.RequestId,
                submissionReference,
                string.Join(',', sites.Select(s => s.CaseReference)),
                changeRequest.OrganisationId
            );
        }

        return Task.FromResult(new ChangeRequestSubmissionResult(submissionReference, sites));
    }

    public Task<NotificationStatusResult> GetNotificationStatusAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    )
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubCaseWorkingApiAdapter.GetNotificationStatusAsync called for applicationId={ApplicationId} workItemId={WorkItemId}",
                application.Id,
                application.CaseManagementWorkItemId
            );
        }

        return Task.FromResult(new NotificationStatusResult(null, null));
    }

    public Task<ResumeFromQueryResult> ResumeFromQueryAsync(
        AccreditationApplicationModel application,
        QuerySubmitterContactDetails contactDetails,
        IReadOnlyList<string> sectionKeys,
        CancellationToken cancellationToken = default
    )
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubCaseWorkingApiAdapter.ResumeFromQueryAsync called for applicationId={ApplicationId} workItemId={WorkItemId} sectionKeys={SectionKeys}",
                application.Id,
                application.CaseManagementWorkItemId,
                string.Join(',', sectionKeys)
            );
        }

        return Task.FromResult(new ResumeFromQueryResult(true));
    }

    public Task<WithdrawResult> WithdrawApplicationAsync(
        AccreditationApplicationModel application,
        QuerySubmitterContactDetails contactDetails,
        string reason,
        CancellationToken cancellationToken = default
    )
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubCaseWorkingApiAdapter.WithdrawApplicationAsync called for applicationId={ApplicationId} workItemId={WorkItemId} reason={Reason} withdrawnBy={WithdrawnBy}",
                application.Id,
                application.CaseManagementWorkItemId,
                reason,
                contactDetails.Email
            );
        }

        return Task.FromResult(WithdrawResult.Succeeded);
    }

    public Task NotifySiteAddedAsync(
        AccreditationApplicationModel application,
        string siteType,
        string orsId,
        string? siteNumber,
        bool isNewSite,
        CancellationToken cancellationToken = default
    )
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubCaseWorkingApiAdapter.NotifySiteAddedAsync called for applicationId={ApplicationId} siteType={SiteType} orsId={OrsId} siteNumber={SiteNumber} isNewSite={IsNewSite}",
                application.Id,
                siteType,
                orsId,
                siteNumber,
                isNewSite
            );
        }

        return Task.CompletedTask;
    }

    private static string ChangeCodeFor(AccreditationChangeRequest changeRequest)
    {
        if (!s_changeTypeCodes.TryGetValue(changeRequest.ChangeType, out var code))
        {
            throw new InvalidOperationException(
                $"No in-year change reference code for change type '{changeRequest.ChangeType}'."
            );
        }

        // The accreditation year is always present on a change request (RA-492), so there is no
        // fallback. A year that is not four digits is refused with a 400, as ManagementBe
        // refuses it.
        if (changeRequest.Year is < MinChangeYear or > MaxChangeYear)
        {
            throw new HttpRequestException(
                $"ManagementBe work item submission failed: {(int)HttpStatusCode.BadRequest} (accreditation year {changeRequest.Year} is not a four-digit year)",
                null,
                HttpStatusCode.BadRequest
            );
        }

        return code;
    }

    // sequence counts from 1.
    internal static string FormatChangeReference(string code, int year, long sequence) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"IYC-{code}-{year:D4}-{(sequence - 1) % MaxChangeSequence + 1:D5}"
        );

    // RA-739: as ManagementBe, SUB-{yyyy}-{seq} with at least four digits; it grows past 9999
    // rather than wrapping. sequence counts from 1.
    internal static string FormatSubmissionReference(int year, long sequence) =>
        string.Create(CultureInfo.InvariantCulture, $"SUB-{year:D4}-{sequence:D4}");

    // RA-503: mirrors the org segment fix applied to ManagementBe's real ApplicationReferenceGenerator
    // - uses the numeric, operator/regulator-safe OrgId (e.g. 500500) rather than OrganisationId
    // (ReEx's internal ObjectId), and no longer truncates to a fixed length, since the Application
    // ID is no longer used as a bank payment reference.
    private static string GenerateReference(AccreditationApplicationModel application)
    {
        var postcode = HttpCaseWorkingApiAdapter.ExtractPostcode(application.SiteAddress);
        // Two digits for a real accreditation year, as the real generator does. The E2E specs use
        // made-up years (3000-3999) on one organisation, registration and material, so outside the
        // 2000s the full year keeps their references apart.
        var year = application.Year is >= 2000 and < 2100
            ? application.Year % 100
            : application.Year;
        var agency = ResolveAgencyCode(postcode);
        var orgNumber = application.OrgId?.ToString("D6") ?? string.Empty;
        var postcodeSuffix = PostcodeSuffix(postcode);
        var materialPrefix = MaterialPrefix(application.MaterialType.ToString());

        return $"AP{year:D2}{agency}{orgNumber}{postcodeSuffix}{materialPrefix}".ToUpperInvariant();
    }

    private static string ResolveAgencyCode(string? postcode)
    {
        var area = ExtractAreaCode(postcode);
        if (area is null)
        {
            return "EA";
        }

        if (area.Equals(NiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "NI";
        }

        if (s_scotlandPrefixes.Contains(area))
        {
            return "SE";
        }

        if (s_walesPrefixes.Contains(area))
        {
            return "NR";
        }

        return "EA";
    }

    private static string? ExtractAreaCode(string? postcode)
    {
        if (string.IsNullOrWhiteSpace(postcode))
        {
            return null;
        }

        var trimmed = postcode.TrimStart();
        var length = 0;
        while (length < trimmed.Length && char.IsLetter(trimmed[length]))
        {
            length++;
        }

        return length == 0 ? null : trimmed[..length];
    }

    private static string PostcodeSuffix(string? postcode)
    {
        if (string.IsNullOrWhiteSpace(postcode))
        {
            return string.Empty;
        }

        var compact = postcode.Replace(" ", string.Empty);
        return compact.Length <= 3 ? compact : compact[^3..];
    }

    private static string MaterialPrefix(string? material)
    {
        if (string.IsNullOrWhiteSpace(material))
        {
            return string.Empty;
        }

        return material.Length <= 2 ? material : material[..2];
    }
}
