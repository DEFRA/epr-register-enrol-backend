using System.Collections.Concurrent;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationChanges;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

// Stub implementation — swapped for a real HTTP adapter once the Case Working Service contract is defined.
public class StubCaseWorkingApiAdapter(
    ILogger<StubCaseWorkingApiAdapter> logger,
    TimeProvider? timeProvider = null
) : ICaseWorkingApiAdapter
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // RA-492: mirrors ManagementBe's in-year change reference, IYC-{code}-{yyyy}-{seq:D5}, where
    // the code names the change type. Unknown change types are rejected, as ManagementBe rejects
    // a work-item type it has not registered.
    private static readonly Dictionary<string, string> s_changeTypeCodes = new()
    {
        [AccreditationChangeRequest.AddOverseasSiteChangeType] = "ORS",
    };

    // Stub only: ManagementBe owns the real sequence, per change code per year. This in-process
    // counter is thread-safe but restarts at 00001 with the app and is not shared between
    // instances. One adapter is registered as a singleton, so a running app never repeats a number.
    private readonly ConcurrentDictionary<(string Code, int Year), int> _changeSequences = new();

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

    public Task<CaseWorkingSubmissionResult> SubmitChangeRequestAsync(
        AccreditationChangeRequest changeRequest,
        AccreditationApplicationModel accreditation,
        CancellationToken cancellationToken = default
    )
    {
        var workItemId = Guid.NewGuid();
        var reference = GenerateChangeReference(changeRequest);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "StubCaseWorkingApiAdapter.SubmitChangeRequestAsync called for requestId={RequestId} reference={Reference} workItemId={WorkItemId} org={OrganisationId}",
                changeRequest.RequestId,
                reference,
                workItemId,
                changeRequest.OrganisationId
            );
        }

        return Task.FromResult(new CaseWorkingSubmissionResult(reference, workItemId));
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

    private string GenerateChangeReference(AccreditationChangeRequest changeRequest)
    {
        if (!s_changeTypeCodes.TryGetValue(changeRequest.ChangeType, out var code))
        {
            throw new InvalidOperationException(
                $"No in-year change reference code for change type '{changeRequest.ChangeType}'."
            );
        }

        // As ManagementBe does: the accreditation year when it is a four-digit year, otherwise the
        // current UTC year.
        var year = changeRequest.Year is >= 1000 and <= 9999
            ? changeRequest.Year
            : _timeProvider.GetUtcNow().UtcDateTime.Year;
        var sequence = _changeSequences.AddOrUpdate((code, year), 1, (_, current) => current + 1);

        return $"IYC-{code}-{year:D4}-{sequence:D5}";
    }

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
