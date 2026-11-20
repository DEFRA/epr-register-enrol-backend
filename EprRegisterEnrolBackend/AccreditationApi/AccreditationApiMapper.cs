using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Status;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolBackend.AccreditationApi;

/// <summary>
/// Builds the accreditation API's v2 responses from a granted accreditation application. This
/// backend holds applications, not accreditations of its own, so a granted application is the
/// accreditation. Nothing is invented beyond the one-entry histories the v2 mapping document
/// prescribes for accreditations that predate effective-dated history: what the application
/// does not hold is left out.
/// </summary>
public static class AccreditationApiMapper
{
    private const string Approved = "approved";

    public static AccreditationSummaryDto ToSummary(AccreditationApplicationModel application) =>
        Summary(application, StatusHistory(application));

    // RA-647: a record that cannot be addressed by its business references (organisation number,
    // registration number, accreditation number) cannot honour the contract, so it is dropped from
    // the response and logged, and the rest of the page is still served. The persistence query
    // already leaves such records out, so this is the second line of defence, and the log line is
    // how an operator learns that one exists.
    public static IReadOnlyList<AccreditationSummaryDto> ToSummaries(
        IEnumerable<AccreditationApplicationModel> applications,
        ILogger logger
    )
    {
        var summaries = new List<AccreditationSummaryDto>();
        foreach (var application in applications)
        {
            var problem = MalformedReason(application);
            if (problem is not null)
            {
                logger.LogWarning(
                    "Dropped a malformed accreditation from the response: applicationId={ApplicationId} reason={Reason}",
                    application.Id,
                    problem
                );
                continue;
            }

            summaries.Add(ToSummary(application));
        }

        return summaries;
    }

    private static string? MalformedReason(AccreditationApplicationModel application)
    {
        if (application.OrgId is null)
            return "no organisation number";
        if (string.IsNullOrWhiteSpace(application.RegistrationReference))
            return "no registration number";
        if (string.IsNullOrWhiteSpace(application.AccreditationReference))
            return "no accreditation number";
        return null;
    }

    // Only the sites included in the accreditation. A site the organisation registered but did
    // not select for it is not on the accreditation, so it is not served.
    public static IEnumerable<OverseasSiteModel> AccreditedSites(
        AccreditationApplicationModel application
    ) => (application.OverseasSites?.Sites ?? []).Where(site => site.Selected);

    public static AccreditationDto ToAccreditation(AccreditationApplicationModel application)
    {
        var history = StatusHistory(application);
        var summary = Summary(application, history);
        var recordedAt = Approval(application);

        return new AccreditationDto
        {
            OrganisationNumber = summary.OrganisationNumber,
            RegistrationNumber = summary.RegistrationNumber,
            AccreditationNumber = summary.AccreditationNumber,
            AccreditationYear = summary.AccreditationYear,
            Material = summary.Material,
            GlassRecyclingProcess = summary.GlassRecyclingProcess,
            WasteProcessingType = summary.WasteProcessingType,
            LatestStatus = summary.LatestStatus,
            TonnageBand = summary.TonnageBand,
            Signatories = application
                .Prns.Authorisers.Select(a => new SignatoryDto
                {
                    FullName = a.FullName,
                    Email = a.Email,
                })
                .ToList(),
            StatusHistory = history,
            OverseasSites = IsExporter(application)
                ? AccreditedSites(application)
                    .OrderBy(site => site.OrsId, StringComparer.Ordinal)
                    .ThenBy(site => site.SiteId)
                    .Select(site => ToOverseasSite(application, site))
                    .ToList()
                : null,
            TonnageBandHistory = summary.TonnageBand is null
                ? []
                :
                [
                    new TonnageBandHistoryEntryDto
                    {
                        TonnageBand = summary.TonnageBand,
                        RecordedAt = recordedAt,
                    },
                ],
        };
    }

    public static AccreditedOverseasSiteDto ToOverseasSite(
        AccreditationApplicationModel application,
        OverseasSiteModel site
    )
    {
        var history = StatusHistory(application);

        return new AccreditedOverseasSiteDto
        {
            SiteId = site.SiteId,
            OrsId = site.OrsId,
            SiteName = site.SiteName,
            FormattedAddress = Blank(site.SiteAddress),
            Address = ToOverseasAddress(site),
            Coordinates = Blank(site.Coordinates),
            Contact = ToContact(site.ContactName, site.ContactEmail, site.ContactPhone),
            OperationCodes = site.OperationCodes,
            RepatriatedLoads = Blank(site.RepatriatedLoads),
            ConditionsOfExport = site.ConditionsOfExport,
            IsEu = site.IsEu,
            IsOecd = site.IsOecd,
            LatestStatus = StatusHistoryBuilder.Latest(history),
            StatusHistory = history,
            InterimSites = site
                .InterimSites.OrderBy(interim => interim.SiteId)
                .Select(interim => ToInterimSite(application, interim))
                .ToList(),
        };
    }

    private static AccreditationSummaryDto Summary(
        AccreditationApplicationModel application,
        IReadOnlyList<StatusHistoryEntryDto> history
    ) =>
        new()
        {
            OrganisationNumber = application.OrgId!.Value,
            RegistrationNumber = application.RegistrationReference!,
            AccreditationNumber = application.AccreditationReference!,
            AccreditationYear = application.Year,
            Material = application.MaterialType.ToString(),
            GlassRecyclingProcess =
                application.MaterialType == MaterialType.Glass
                    ? application.GlassRecyclingProcess switch
                    {
                        GlassRecyclingProcess.Remelt => "glass_re_melt",
                        GlassRecyclingProcess.Other => "glass_other",
                        _ => null,
                    }
                    : null,
            WasteProcessingType = IsExporter(application) ? "exporter" : "reprocessor",
            LatestStatus = StatusHistoryBuilder.Latest(history),
            TonnageBand = application.Prns.PlannedTonnageBand switch
            {
                PlannedTonnageBand.UpTo500 => "up_to_500",
                PlannedTonnageBand.UpTo5000 => "up_to_5000",
                PlannedTonnageBand.UpTo10000 => "up_to_10000",
                PlannedTonnageBand.Over10000 => "over_10000",
                _ => null,
            },
        };

    // No status changes are held yet, so an accreditation, or one of its sites, has the single
    // `approved` entry the v2 mapping document prescribes for records that predate them. It is
    // recorded when the application was approved (the last case management status push, else the
    // last edit). The dates follow from that: see StatusHistoryBuilder.
    private static List<StatusHistoryEntryDto> StatusHistory(
        AccreditationApplicationModel application
    ) => StatusHistoryBuilder.Build([(Approved, Approval(application))], application.Year);

    private static DateTime Approval(AccreditationApplicationModel application) =>
        AsUtc(application.CaseManagementStatusUpdatedAt ?? application.DateLastEdited);

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

    // RA-781: an interim site is not approved as such. It is approved from when its overseas site was
    // approved (not CreatedAt: it takes effect with the application that adds it), withdrawn from each
    // withdrawal and approved again from each restore. The changes come from the stored history; a
    // site with none stored is approved, then withdrawn from RemovedAt if it is withdrawn now. A change
    // recorded before the approval applies from the approval, as the site cannot be withdrawn before it
    // is in the accreditation. Their dates follow StatusHistoryBuilder's rules, as an accreditation's do.
    private static List<StatusHistoryEntryDto> InterimStatusHistory(
        AccreditationApplicationModel application,
        InterimSiteModel interim
    )
    {
        var approval = Approval(application);
        var changes = new List<(string Status, DateTime RecordedAt)> { (Approved, approval) };
        if (interim.StatusHistory.Count > 0)
        {
            // The first stored entry is the site being added: it is the approval itself.
            changes.AddRange(
                interim.StatusHistory.Skip(1).Select(change => (change.Status, AsUtc(change.RecordedAt)))
            );
        }
        else if (interim.RemovedAt is { } removed)
        {
            changes.Add((InterimSiteStatusHistory.Withdrawn, AsUtc(removed)));
        }

        changes = changes
            .Select(change => (change.Status, change.RecordedAt < approval ? approval : change.RecordedAt))
            .ToList();

        return StatusHistoryBuilder.Build(changes, application.Year);
    }

    private static InterimSiteDto ToInterimSite(
        AccreditationApplicationModel application,
        InterimSiteModel interim
    )
    {
        var history = InterimStatusHistory(application, interim);
        return new InterimSiteDto
        {
            SiteId = interim.SiteId,
            SiteNumber = interim.SiteNumber,
            SiteName = interim.SiteName,
            Address = new AddressDto
            {
                Line1 = interim.AddressLine1,
                Line2 = Blank(interim.AddressLine2),
                Town = interim.TownOrCity,
                Country = interim.Country,
                Postcode = Blank(interim.Postcode),
                Region = Blank(interim.StateOrRegion),
            },
            Contact = new ContactDetailsDto
            {
                FullName = interim.ContactName,
                Email = interim.ContactEmail,
                Phone = interim.ContactPhone,
            },
            OperationCodes = interim.OperationCodes,
            CreatedAt = interim.CreatedAt is { } created ? AsUtc(created) : null,
            LatestStatus = StatusHistoryBuilder.Latest(history),
            StatusHistory = history,
        };
    }

    private static AddressDto? ToOverseasAddress(OverseasSiteModel site)
    {
        var address = new AddressDto
        {
            Line1 = Blank(site.AddressLine1),
            Line2 = Blank(site.AddressLine2),
            Town = Blank(site.TownOrCity),
            Country = Blank(site.Country),
        };

        return
            address.Line1 is null
            && address.Line2 is null
            && address.Town is null
            && address.Country is null
            ? null
            : address;
    }

    private static ContactDetailsDto? ToContact(string? name, string? email, string? phone)
    {
        var contact = new ContactDetailsDto
        {
            FullName = Blank(name),
            Email = Blank(email),
            Phone = Blank(phone),
        };

        return contact.FullName is null && contact.Email is null && contact.Phone is null
            ? null
            : contact;
    }

    private static bool IsExporter(AccreditationApplicationModel application) =>
        string.IsNullOrWhiteSpace(application.WasteProcessingType)
            ? application.IsExporter
            : string.Equals(
                application.WasteProcessingType,
                "exporter",
                StringComparison.OrdinalIgnoreCase
            );

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
