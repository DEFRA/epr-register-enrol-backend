using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Seeding;

/// <summary>
/// Copies what a seed's ReEx response holds onto the domain model entities (design doc §6, phase 2). Each
/// <c>ApplyTo</c> writes only the fields ReEx sent, onto a new entity or the current version alike, so a
/// re-seed never clears data this service owns (status, history, interim sites) or that enrichment added.
/// Unknown values stay as they are: data is never invented.
/// </summary>
public static class SeedEntityMapper
{
    public static void ApplyTo(OrganisationEntity target, ReExAccreditationDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.OrganisationName))
        {
            target.Name = dto.OrganisationName;
        }

        if (!string.IsNullOrWhiteSpace(dto.CompaniesHouseNumber))
        {
            target.CompaniesHouseNumber = dto.CompaniesHouseNumber;
        }

        MarkFromReEx(target.Source);
    }

    public static void ApplyTo(
        RegistrationEntity target,
        ReExAccreditationDto dto,
        string reExRegistrationId,
        MaterialType material
    )
    {
        target.ReExRegistrationId = reExRegistrationId;
        target.Material = material;
        target.Nation = dto.Nation;
        target.GlassRecyclingProcess = dto.GlassRecyclingProcess;
        target.WasteProcessingType = dto.IsExporter
            ? WasteProcessingKind.Exporter
            : WasteProcessingKind.Reprocessor;
        if (!string.IsNullOrWhiteSpace(dto.RegistrationReference))
        {
            target.RegistrationNumber = dto.RegistrationReference;
        }

        MarkFromReEx(target.Source);
    }

    public static void ApplyTo(OverseasSiteEntity target, OverseasSiteModel site)
    {
        target.OrsId = site.OrsId;
        target.SiteName = site.SiteName;
        target.FormattedAddress = site.SiteAddress;
        target.Address = Address(site);
        target.Country = site.Country;
        target.IsEu = site.IsEu;
        target.IsOecd = site.IsOecd;
        target.Coordinates = site.Coordinates;
        target.BaselOecdCodes = new[] { site.Code1, site.Code2, site.Code3 }
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .ToList();
    }

    /// <summary>A site from ReEx's ORS-A is already registered there, so it starts Active (§6.1 step 2).</summary>
    public static OverseasSiteEntity NewReExSite(ObjectId registrationId, OverseasSiteModel site)
    {
        var entity = new OverseasSiteEntity
        {
            RegistrationId = registrationId,
            Origin = SiteOrigin.ReEx,
            Status = OverseasSiteStatus.Active,
        };
        ApplyTo(entity, site);
        return entity;
    }

    private static PostalAddress? Address(OverseasSiteModel site) =>
        site.AddressLine1 is null
        && site.AddressLine2 is null
        && site.TownOrCity is null
        && site.Country is null
            ? null
            : new PostalAddress
            {
                Line1 = site.AddressLine1,
                Line2 = site.AddressLine2,
                TownOrCity = site.TownOrCity,
                Country = site.Country,
            };

    private static void MarkFromReEx(EntitySource source)
    {
        source.System = SourceSystem.ReEx;
        source.EnrichmentPending = true;
    }
}
