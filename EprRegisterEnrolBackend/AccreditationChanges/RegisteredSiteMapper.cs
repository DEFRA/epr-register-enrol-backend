using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-697: a registration's domain-model ORS in the site shape the accreditation path returns, so a
/// registration-only view reuses the frontend's view code. Registered, never accredited: there is no accreditation.
/// </summary>
public static class RegisteredSiteMapper
{
    /// <summary>
    /// The registration's sites, in ORS id order. A domain-model ORS has no ReEx site key, so the site id is the ORS
    /// id's integer value, which registration-only change request items also store; an ORS without a numeric ORS id
    /// cannot be keyed and is left out.
    /// </summary>
    public static List<OverseasSiteModel> ToSites(IEnumerable<OverseasSiteEntity> overseasSites) =>
        overseasSites
            .Select(ors => (Ors: ors, SiteId: SiteIdOf(ors.OrsId)))
            .Where(s => s.SiteId is not null)
            .OrderBy(s => s.SiteId)
            .Select(s => ToSite(s.Ors, s.SiteId!.Value))
            .ToList();

    public static int? SiteIdOf(string? number) =>
        int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    private static OverseasSiteModel ToSite(OverseasSiteEntity ors, int siteId)
    {
        var codes = ors.BaselOecdCodes;
        var site = new OverseasSiteModel
        {
            SiteId = siteId,
            OrsId = ors.OrsId,
            SiteName = ors.SiteName ?? string.Empty,
            SiteAddress = ors.FormattedAddress,
            AddressLine1 = ors.Address?.Line1,
            AddressLine2 = ors.Address?.Line2,
            TownOrCity = ors.Address?.TownOrCity,
            Country = ors.Country,
            Coordinates = ors.Coordinates,
            ContactName = ors.Contact?.FullName,
            ContactEmail = ors.Contact?.Email,
            ContactPhone = ors.Contact?.Phone,
            OperationCodes = [.. ors.OperationCodes],
            Code1 = codes.ElementAtOrDefault(0),
            Code2 = codes.ElementAtOrDefault(1),
            Code3 = codes.ElementAtOrDefault(2),
            RepatriatedLoads = ors.RepatriatedLoads,
            ConditionsOfExport = ors.ConditionsOfExport,
            IsEu = ors.IsEu,
            IsOecd = ors.IsOecd,
            Selected = false,
            IsNewSite = false,
            InterimSites = ors
                .InterimSites.Where(i => i.Status == InterimSiteStatus.Active)
                .Select(ToInterimSite)
                .ToList(),
        };
        InterimSiteSync.SyncMirror(site);
        return site;
    }

    // Active ones only: a proposed, withdrawn or discarded interim site is not part of the registration.
    private static InterimSiteModel ToInterimSite(InterimSiteEntry interim) =>
        new()
        {
            SiteId = SiteIdOf(interim.SiteNumber) ?? 0,
            SiteNumber = interim.SiteNumber,
            SiteName = interim.SiteName ?? string.Empty,
            AddressLine1 = interim.Address?.Line1 ?? string.Empty,
            AddressLine2 = interim.Address?.Line2,
            TownOrCity = interim.Address?.TownOrCity ?? string.Empty,
            StateOrRegion = interim.Address?.Region,
            Postcode = interim.Address?.Postcode,
            Country = interim.Address?.Country ?? string.Empty,
            ContactName = interim.Contact?.FullName ?? string.Empty,
            ContactEmail = interim.Contact?.Email ?? string.Empty,
            ContactPhone = interim.Contact?.Phone ?? string.Empty,
            OperationCodes = [.. interim.OperationCodes],
            CreatedAt = interim.CreatedAt,
        };
}
