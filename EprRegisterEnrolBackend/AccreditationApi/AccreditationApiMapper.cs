using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Status;

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

    public static AccreditationSummaryDto ToSummary(
        AccreditationApplicationModel application,
        DateTimeOffset now
    ) => Summary(application, now, StatusHistory(application, null));

    public static AccreditationDto ToAccreditation(
        AccreditationApplicationModel application,
        DateTimeOffset now
    )
    {
        var history = StatusHistory(application, null);
        var summary = Summary(application, now, history);
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
            Status = summary.Status,
            TonnageBand = summary.TonnageBand,
            Signatories = application
                .Prns.Authorisers.Select(a => new SignatoryDto
                {
                    FullName = a.FullName,
                    Email = a.Email,
                })
                .ToList(),
            StatusHistory = history,
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
        OverseasSiteModel site,
        bool includeWithdrawn,
        DateTimeOffset now
    )
    {
        var history = StatusHistory(application, site);

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
            ValidFrom = ParseInstant(site.ValidFrom),
            Status = Project(history, now),
            StatusHistory = history,
            InterimSites = site
                .InterimSites.Where(interim => includeWithdrawn || interim.RemovedAt is null)
                .OrderBy(interim => interim.SiteId)
                .Select(ToInterimSite)
                .ToList(),
        };
    }

    private static AccreditationSummaryDto Summary(
        AccreditationApplicationModel application,
        DateTimeOffset now,
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
            Status = Project(history, now),
            TonnageBand = application.Prns.PlannedTonnageBand switch
            {
                PlannedTonnageBand.UpTo500 => "up_to_500",
                PlannedTonnageBand.UpTo5000 => "up_to_5000",
                PlannedTonnageBand.UpTo10000 => "up_to_10000",
                PlannedTonnageBand.Over10000 => "over_10000",
                _ => null,
            },
        };

    // No effective-dated history is held yet, so an accreditation, or one of its sites, has the
    // single `approved` entry the v2 mapping document prescribes for records that predate it.
    // It is recorded when the application was approved (the last case management status push,
    // else the last edit). A site takes effect on its ReEx approval date when it has one.
    private static List<StatusHistoryEntryDto> StatusHistory(
        AccreditationApplicationModel application,
        OverseasSiteModel? site
    )
    {
        var recordedAt = Approval(application);
        var effective = site is null ? recordedAt : ParseInstant(site.ValidFrom) ?? recordedAt;

        return
        [
            new StatusHistoryEntryDto
            {
                Status = Approved,
                EffectiveDate = UkDate(effective),
                RecordedAt = recordedAt,
            },
        ];
    }

    // The status for today: the latest entry whose effective date is on or before today. A
    // record always starts out approved, so a first entry that is not yet effective still
    // projects as approved rather than as nothing.
    private static string Project(IReadOnlyList<StatusHistoryEntryDto> history, DateTimeOffset now)
    {
        var today = UkDate(now.UtcDateTime);
        return history
                .Where(entry => entry.EffectiveDate <= today)
                .OrderBy(entry => entry.EffectiveDate)
                .ThenBy(entry => entry.RecordedAt)
                .Select(entry => entry.Status)
                .LastOrDefault()
            ?? Approved;
    }

    private static DateTime Approval(AccreditationApplicationModel application) =>
        AsUtc(application.CaseManagementStatusUpdatedAt ?? application.DateLastEdited);

    private static DateOnly UkDate(DateTime utc) => UkCalendar.ToUkDate(utc);

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

    // ValidFrom is held as the raw ISO string ReEx sent; unparseable is treated as absent.
    private static DateTime? ParseInstant(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed
        )
            ? parsed.UtcDateTime
            : null;

    private static InterimSiteDto ToInterimSite(InterimSiteModel interim) =>
        new()
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
            RemovedAt = interim.RemovedAt is { } removed ? AsUtc(removed) : null,
        };

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
