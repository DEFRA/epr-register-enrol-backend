using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Entities.Sections;
using MongoDB.Bson;
using DomainQuerySubmission = EprRegisterEnrolBackend.DomainModel.Entities.QuerySubmission;

namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>
/// Copies a legacy application onto an <see cref="ApplicationEntity"/> (design doc §6.1 step 4). The overseas site
/// entries are built by <see cref="ApplicationGroupMigrator"/>, which knows the ORS each site maps to.
/// </summary>
public static class LegacyApplicationMapper
{
    /// <summary>The application's version 1, whose <c>entityId</c> is the legacy <c>_id</c> (Q33).</summary>
    public static ApplicationEntity ToEntity(
        AccreditationApplicationModel legacy,
        ObjectId? registrationId,
        List<ApplicationOverseasSiteEntry> overseasSites
    ) =>
        new()
        {
            EntityId = legacy.Id ?? throw new ArgumentException("The application has no id."),
            RegistrationId = registrationId,
            ReExOrganisationId = legacy.OrganisationId,
            ReExRegistrationId = legacy.RegistrationId,
            Year = legacy.Year,
            Status = legacy.ApplicationStatus,
            ApplicationReference = legacy.ApplicationReference,
            PaymentReference = legacy.PaymentReference,
            CaseManagementWorkItemId = legacy.CaseManagementWorkItemId,
            CaseManagementStatusUpdatedAt = legacy.CaseManagementStatusUpdatedAt,
            WithdrawalReason = legacy.WithdrawalReason,
            DateSent = legacy.DateSent,
            DateLastEdited = legacy.DateLastEdited,
            ReservedAccreditationNumber = legacy.AccreditationReference,
            ApplicationOverseasSites = overseasSites,
            SubmittedBy = legacy.SubmittedBy is null
                ? null
                : new Submitter
                {
                    FullName = legacy.SubmittedBy.FullName,
                    JobTitle = legacy.SubmittedBy.JobTitle,
                    Email = legacy.SubmittedBy.Email,
                },
            Query = legacy.Query is null ? null : ToQuery(legacy.Query),
            Prns = ToSection(legacy.Prns),
            BusinessPlan = ToSection(legacy.BusinessPlan),
            SamplingPlan = ToSection(legacy.SamplingPlan),
            BesEvidence = legacy.BesEvidence is null
                ? null
                : new BesEvidenceSection
                {
                    SectionStatus = legacy.BesEvidence.SectionStatus,
                    Versions = legacy
                        .BesEvidence.Versions.Select(snapshot => new BesEvidenceSectionSnapshot
                        {
                            VersionedAt = snapshot.VersionedAt,
                        })
                        .ToList(),
                },
            OverseasSitesSection = legacy.OverseasSites is null
                ? null
                : new OverseasSitesSection
                {
                    SectionStatus = legacy.OverseasSites.SectionStatus,
                    Versions = legacy
                        .OverseasSites.Versions.Select(snapshot => new OverseasSitesSectionSnapshot
                        {
                            VersionedAt = snapshot.VersionedAt,
                            Sites = snapshot.Sites.Select(ToSubmitted).ToList(),
                        })
                        .ToList(),
                },
        };

    /// <summary>
    /// One site's entry on the Application. The caller knows the ORS it maps to, the overlay it carries, and the
    /// model number each legacy interim site number has.
    /// </summary>
    public static ApplicationOverseasSiteEntry ToSiteEntry(
        OverseasSiteModel site,
        ObjectId? overseasSiteId,
        BsonDocument? proposedChanges,
        Func<string, string> interimSiteNumber
    ) =>
        new()
        {
            SiteId = site.SiteId,
            OverseasSiteId = overseasSiteId,
            Selected = site.Selected,
            IsNewSite = site.IsNewSite,
            RegisteredNowAccredited = site.RegisteredNowAccredited,
            ProposedChanges = proposedChanges,
            PreviousSites = site
                .PreviousSites.Select(p => ToSubmitted(p).ToBsonDocument())
                .ToList(),
            BesEvidence = ToBesEvidence(site.BesEvidence),
            InterimSites = InterimSiteSync
                .All(site)
                .Select(i => new ApplicationInterimSiteEntry
                {
                    LegacySiteId = i.SiteId,
                    SiteNumber = interimSiteNumber(i.SiteNumber),
                    IsNewSite = i.IsNewSite,
                })
                .ToList(),
        };

    /// <summary>A site's BES evidence, or null when it has none.</summary>
    public static BesEvidence? ToBesEvidence(BesEvidenceModel? legacy) =>
        legacy is null
            ? null
            : new BesEvidence
            {
                DoYouWantToUploadMoreEvidence = legacy.DoYouWantToUploadMoreEvidence,
                BesEvidenceUploads = legacy
                    .BesEvidenceUploads.Select(file => new BesEvidenceFile
                    {
                        FileId = file.FileId,
                        Filename = file.Filename,
                        ContentType = file.ContentType,
                        ScanStatus = file.ScanStatus,
                        BesEvidenceValidFromDate = file.BesEvidenceValidFromDate,
                        BesEvidenceExpiryDate = file.BesEvidenceExpiryDate,
                        UploadedAt = file.UploadedAt,
                        UploadedBy = file.UploadedBy,
                        S3Key = file.S3Key,
                        S3Bucket = file.S3Bucket,
                    })
                    .ToList(),
            };

    /// <summary>An overseas site as it was submitted, without its domain model foreign key.</summary>
    public static SubmittedOverseasSite ToSubmitted(OverseasSiteModel site) =>
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
            OperationCodes = [.. site.OperationCodes],
            Code1 = site.Code1,
            Code2 = site.Code2,
            Code3 = site.Code3,
            RepatriatedLoads = site.RepatriatedLoads,
            ConditionsOfExport = site.ConditionsOfExport,
            IsEu = site.IsEu,
            IsOecd = site.IsOecd,
            ValidFrom = site.ValidFrom,
            Selected = site.Selected,
            BesEvidence = ToBesEvidence(site.BesEvidence),
            IsNewSite = site.IsNewSite,
            RegisteredNowAccredited = site.RegisteredNowAccredited,
            PreviousSites = site.PreviousSites.Select(ToSubmitted).ToList(),
            InterimSite = site.InterimSite is null ? null : ToSubmitted(site.InterimSite),
            InterimSites = site.InterimSites.Select(ToSubmitted).ToList(),
        };

    /// <summary>An interim site as it was submitted.</summary>
    public static SubmittedInterimSite ToSubmitted(InterimSiteModel site) =>
        new()
        {
            SiteId = site.SiteId,
            SiteNumber = site.SiteNumber,
            Country = site.Country,
            SiteName = site.SiteName,
            AddressLine1 = site.AddressLine1,
            AddressLine2 = site.AddressLine2,
            TownOrCity = site.TownOrCity,
            StateOrRegion = site.StateOrRegion,
            Postcode = site.Postcode,
            ContactName = site.ContactName,
            ContactEmail = site.ContactEmail,
            ContactPhone = site.ContactPhone,
            OperationCodes = [.. site.OperationCodes],
            IsNewSite = site.IsNewSite,
            CreatedAt = site.CreatedAt,
            RemovedAt = site.RemovedAt,
        };

    /// <summary>An uploaded file, with its enums stored as strings.</summary>
    public static ApplicationFile ToFile(AccreditationApplicationFile file) =>
        new()
        {
            FileId = file.FileId,
            Filename = file.Filename,
            ContentType = file.ContentType,
            UploadedAt = file.UploadedAt,
            UploadedByUserId = file.UploadedByUserId,
            ScanStatus = file.ScanStatus,
            DocumentType = file.DocumentType,
            S3Key = file.S3Key,
            S3Bucket = file.S3Bucket,
        };

    private static ApplicationQuery ToQuery(AccreditationApplicationQuery query) =>
        new()
        {
            QueryNote = query.QueryNote,
            QueriedSectionKeys = [.. query.QueriedSectionKeys],
            QuerySubmissions = query
                .QuerySubmissions.Select(submission => new DomainQuerySubmission
                {
                    QuerySubmissionTime = submission.QuerySubmissionTime,
                    SectionKeys = [.. submission.SectionKeys],
                    QuerySubmitterContactDetails = new QuerySubmitter
                    {
                        FullName = submission.QuerySubmitterContactDetails.FullName,
                        Email = submission.QuerySubmitterContactDetails.Email,
                        Role = submission.QuerySubmitterContactDetails.Role,
                    },
                })
                .ToList(),
        };

    private static PrnsSection ToSection(AccreditationApplicationPrns prns) =>
        new()
        {
            PlannedTonnageBand = TonnageBandConversion.FromLegacy(prns.PlannedTonnageBand),
            Authorisers = prns.Authorisers.Select(ToAuthoriser).ToList(),
            SectionStatus = prns.SectionStatus,
            Versions = prns
                .Versions.Select(snapshot => new PrnsSectionSnapshot
                {
                    PlannedTonnageBand = TonnageBandConversion.FromLegacy(
                        snapshot.PlannedTonnageBand
                    ),
                    Authorisers = snapshot.Authorisers.Select(ToAuthoriser).ToList(),
                    VersionedAt = snapshot.VersionedAt,
                })
                .ToList(),
        };

    private static BusinessPlanSection ToSection(AccreditationApplicationBusinessPlan plan) =>
        new()
        {
            NewInfrastructurePercent = plan.NewInfrastructurePercent,
            PriceSupportPercent = plan.PriceSupportPercent,
            BusinessCollectionsPercent = plan.BusinessCollectionsPercent,
            CommunicationsPercent = plan.CommunicationsPercent,
            NewMarketsPercent = plan.NewMarketsPercent,
            NewUsesPercent = plan.NewUsesPercent,
            OtherPercent = plan.OtherPercent,
            NewInfrastructureDetail = plan.NewInfrastructureDetail,
            PriceSupportDetail = plan.PriceSupportDetail,
            BusinessCollectionsDetail = plan.BusinessCollectionsDetail,
            CommunicationsDetail = plan.CommunicationsDetail,
            NewMarketsDetail = plan.NewMarketsDetail,
            NewUsesDetail = plan.NewUsesDetail,
            OtherDetail = plan.OtherDetail,
            SectionStatus = plan.SectionStatus,
            Versions = plan
                .Versions.Select(snapshot => new BusinessPlanSectionSnapshot
                {
                    NewInfrastructurePercent = snapshot.NewInfrastructurePercent,
                    PriceSupportPercent = snapshot.PriceSupportPercent,
                    BusinessCollectionsPercent = snapshot.BusinessCollectionsPercent,
                    CommunicationsPercent = snapshot.CommunicationsPercent,
                    NewMarketsPercent = snapshot.NewMarketsPercent,
                    NewUsesPercent = snapshot.NewUsesPercent,
                    OtherPercent = snapshot.OtherPercent,
                    NewInfrastructureDetail = snapshot.NewInfrastructureDetail,
                    PriceSupportDetail = snapshot.PriceSupportDetail,
                    BusinessCollectionsDetail = snapshot.BusinessCollectionsDetail,
                    CommunicationsDetail = snapshot.CommunicationsDetail,
                    NewMarketsDetail = snapshot.NewMarketsDetail,
                    NewUsesDetail = snapshot.NewUsesDetail,
                    OtherDetail = snapshot.OtherDetail,
                    VersionedAt = snapshot.VersionedAt,
                })
                .ToList(),
        };

    private static SamplingPlanSection ToSection(AccreditationApplicationSamplingPlan plan) =>
        new()
        {
            Files = plan.Files.Select(ToFile).ToList(),
            SectionStatus = plan.SectionStatus,
            Versions = plan
                .Versions.Select(snapshot => new SamplingPlanSectionSnapshot
                {
                    Files = snapshot.Files.Select(ToFile).ToList(),
                    VersionedAt = snapshot.VersionedAt,
                })
                .ToList(),
        };

    private static Authoriser ToAuthoriser(PrnsAuthoriser authoriser) =>
        new()
        {
            FullName = authoriser.FullName,
            Email = authoriser.Email,
            IsNew = authoriser.IsNew,
        };
}
