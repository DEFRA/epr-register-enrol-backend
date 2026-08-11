using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

// Stub implementation — swapped for a real HTTP adapter once the Case Working Service contract is defined.
public class StubCaseWorkingApiAdapter(ILogger<StubCaseWorkingApiAdapter> logger)
    : ICaseWorkingApiAdapter
{
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

        logger.LogInformation(
            "StubCaseWorkingApiAdapter.SubmitApplicationAsync called for applicationId={ApplicationId} generatedRef={ApplicationReference} workItemId={WorkItemId} org={OrganisationId}",
            application.Id,
            applicationReference,
            workItemId,
            application.OrganisationId
        );

        return Task.FromResult(new CaseWorkingSubmissionResult(applicationReference, workItemId));
    }

    public Task<NotificationStatusResult> GetNotificationStatusAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    )
    {
        logger.LogInformation(
            "StubCaseWorkingApiAdapter.GetNotificationStatusAsync called for applicationId={ApplicationId} workItemId={WorkItemId}",
            application.Id,
            application.CaseManagementWorkItemId
        );

        return Task.FromResult(new NotificationStatusResult(null, null));
    }

    public Task<ResumeFromQueryResult> ResumeFromQueryAsync(
        AccreditationApplicationModel application,
        QuerySubmitterContactDetails contactDetails,
        IReadOnlyList<string> sectionKeys,
        CancellationToken cancellationToken = default
    )
    {
        logger.LogInformation(
            "StubCaseWorkingApiAdapter.ResumeFromQueryAsync called for applicationId={ApplicationId} workItemId={WorkItemId} sectionKeys={SectionKeys}",
            application.Id,
            application.CaseManagementWorkItemId,
            string.Join(',', sectionKeys)
        );

        return Task.FromResult(new ResumeFromQueryResult(true));
    }

    public Task<WithdrawResult> WithdrawApplicationAsync(
        AccreditationApplicationModel application,
        QuerySubmitterContactDetails contactDetails,
        string reason,
        CancellationToken cancellationToken = default
    )
    {
        logger.LogInformation(
            "StubCaseWorkingApiAdapter.WithdrawApplicationAsync called for applicationId={ApplicationId} workItemId={WorkItemId} reason={Reason} withdrawnBy={WithdrawnBy}",
            application.Id,
            application.CaseManagementWorkItemId,
            reason,
            contactDetails.Email
        );

        return Task.FromResult(new WithdrawResult(true));
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
        logger.LogInformation(
            "StubCaseWorkingApiAdapter.NotifySiteAddedAsync called for applicationId={ApplicationId} siteType={SiteType} orsId={OrsId} siteNumber={SiteNumber} isNewSite={IsNewSite}",
            application.Id,
            siteType,
            orsId,
            siteNumber,
            isNewSite
        );

        return Task.CompletedTask;
    }

    private static string GenerateReference(AccreditationApplicationModel application)
    {
        const int maxLength = 18;
        var postcode = HttpCaseWorkingApiAdapter.ExtractPostcode(application.SiteAddress);
        var year = application.Year % 100;
        var agency = ResolveAgencyCode(postcode);
        var organisationId = application.OrganisationId;
        var postcodeSuffix = PostcodeSuffix(postcode);
        var materialPrefix = MaterialPrefix(application.MaterialType.ToString());

        var reference =
            $"AP{year:D2}{agency}{organisationId}{postcodeSuffix}{materialPrefix}".ToUpperInvariant();

        return reference.Length > maxLength ? reference[..maxLength] : reference;
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
