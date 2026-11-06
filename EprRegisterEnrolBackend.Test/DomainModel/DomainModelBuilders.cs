using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Entities.Sections;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.DomainModel;

/// <summary>
/// Plausible, valid entities for domain model tests. Each builder returns an unsaved object with its
/// references filled in from the arguments, so a test can build a whole registration tree in a few lines.
/// Counters keep business keys unique across a test unless the test overrides them.
/// </summary>
public static class DomainModelBuilders
{
    private static int s_counter;

    private static int Next() => Interlocked.Increment(ref s_counter);

    public static OrganisationEntity Organisation(int? organisationNumber = null) =>
        new()
        {
            ReExOrganisationId = ObjectId.GenerateNewId().ToString(),
            OrganisationNumber = organisationNumber ?? 500000 + Next(),
            Name = "Test Organisation Ltd",
            CompaniesHouseNumber = "01234567",
            BusinessType = "Limited company",
            SubmittedToRegulator = "ea",
            UpdatedAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    public static RegistrationEntity Registration(
        ObjectId organisationId,
        string? registrationNumber = null,
        string? reExRegistrationId = null
    ) =>
        new()
        {
            OrganisationId = organisationId,
            ReExRegistrationId = reExRegistrationId ?? ObjectId.GenerateNewId().ToString(),
            RegistrationNumber = registrationNumber ?? $"R27EX{Next():D7}P",
            Status = RegistrationStatus.Approved,
            WasteProcessingType = WasteProcessingKind.Exporter,
            Material = MaterialType.Plastic,
            Nation = Nation.England,
            Site = new RegistrationSite { Address = Address(), SiteId = "1" },
            Source = new EntitySource
            {
                System = SourceSystem.Application,
                EnrichmentPending = true,
            },
        };

    public static OverseasSiteEntity OverseasSite(
        ObjectId registrationId,
        string? orsId = null,
        params string[] interimSiteNumbers
    ) =>
        new()
        {
            RegistrationId = registrationId,
            OrsId = orsId ?? $"{Next() % 999 + 1:D3}",
            SiteName = "Overseas Reprocessing Ltd",
            Address = Address(),
            Country = "Turkey",
            IsOecd = true,
            OperationCodes = ["R3"],
            BaselOecdCodes = ["B3010", "B3020"],
            Status = OverseasSiteStatus.Active,
            Origin = SiteOrigin.ReEx,
            InterimSites = interimSiteNumbers.Select(InterimSite).ToList(),
        };

    public static InterimSiteEntry InterimSite(string siteNumber) =>
        new()
        {
            SiteNumber = siteNumber,
            SiteName = "Interim Storage Ltd",
            Address = Address(),
            OperationCodes = ["R12"],
            Status = InterimSiteStatus.Active,
            CreatedAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    public static ApplicationEntity Application(
        ObjectId? registrationId = null,
        string? applicationReference = null
    ) =>
        new()
        {
            RegistrationId = registrationId,
            ReExOrganisationId = ObjectId.GenerateNewId().ToString(),
            ReExRegistrationId = ObjectId.GenerateNewId().ToString(),
            Status = ApplicationStatus.Submitted,
            ApplicationReference = applicationReference ?? $"APP-{Next():D6}",
            DateLastEdited = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Details = AccreditationDetails(),
        };

    public static AccreditationDetails AccreditationDetails() =>
        new()
        {
            Kind = AccreditationKind.Reapply,
            Year = 2027,
            Prns = new PrnsSection
            {
                PlannedTonnageBand = TonnageBand.UpTo10000,
                SectionStatus = SectionStatus.Submitted,
                Versions =
                [
                    new PrnsSectionSnapshot
                    {
                        PlannedTonnageBand = TonnageBand.UpTo500,
                        VersionedAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    },
                ],
            },
            BusinessPlan = new BusinessPlanSection
            {
                NewInfrastructurePercent = 100,
                SectionStatus = SectionStatus.Completed,
            },
            SamplingPlan = new SamplingPlanSection
            {
                Files = [File()],
                SectionStatus = SectionStatus.Queried,
            },
            BesEvidence = new BesEvidenceSection { SectionStatus = SectionStatus.InProgress },
            OverseasSitesSection = new OverseasSitesSection
            {
                SectionStatus = SectionStatus.NotStarted,
            },
            ApplicationOverseasSites =
            [
                new ApplicationOverseasSiteEntry
                {
                    SiteId = 1,
                    Selected = true,
                    InterimSites =
                    [
                        new ApplicationInterimSiteEntry { LegacySiteId = 1, SiteNumber = "001" },
                    ],
                },
            ],
        };

    /// <summary>An application of any type on <paramref name="registrationId"/>, with the given details.</summary>
    public static ApplicationEntity Application(
        ObjectId? registrationId,
        ApplicationDetails details,
        ApplicationStatus status = ApplicationStatus.Submitted
    ) =>
        new()
        {
            RegistrationId = registrationId,
            ReExOrganisationId = ObjectId.GenerateNewId().ToString(),
            Status = status,
            ApplicationReference = $"APP-{Next():D6}",
            DateLastEdited = new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Details = details,
        };

    /// <summary>An in-year change to one ORS, holding that ORS (and any interim sites given) while open.</summary>
    public static OrsChangeDetails OrsChangeDetails(
        ObjectId overseasSiteId,
        ObjectId? targetAccreditationId = null,
        params string[] interimSiteNumbers
    ) =>
        new()
        {
            TargetAccreditationId = targetAccreditationId ?? ObjectId.GenerateNewId(),
            OverseasSiteId = overseasSiteId,
            ChangeKinds = [OrsChangeKind.EditSite, OrsChangeKind.ChangeInterimSites],
            Declaration = new OrsChangeDeclaration
            {
                DeclaredBy = new Submitter { FullName = "A Declarer", JobTitle = "Director" },
                DeclaredAt = new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            Site = new ApplicationOverseasSiteEntry
            {
                SiteId = 1,
                OverseasSiteId = overseasSiteId,
                InterimSites = interimSiteNumbers
                    .Select(
                        (number, i) =>
                            new ApplicationInterimSiteEntry
                            {
                                LegacySiteId = i + 1,
                                SiteNumber = number,
                            }
                    )
                    .ToList(),
            },
        };

    public static TonnageChangeDetails TonnageChangeDetails(
        ObjectId? targetAccreditationId = null
    ) =>
        new()
        {
            TargetAccreditationId = targetAccreditationId ?? ObjectId.GenerateNewId(),
            FromBand = TonnageBand.UpTo5000,
            ToBand = TonnageBand.UpTo10000,
        };

    public static NewRegistrationDetails NewRegistrationDetails() =>
        new()
        {
            Sections =
            [
                new RegistrationJourneySection
                {
                    Name = "siteDetails",
                    Status = SectionStatus.Completed,
                },
                new RegistrationJourneySection
                {
                    Name = "wasteDetails",
                    Status = SectionStatus.InProgress,
                },
            ],
        };

    public static AnnualChargeDetails AnnualChargeDetails(int chargeYear = 2027) =>
        new() { ChargeYear = chargeYear };

    public static AccreditationEntity Accreditation(
        ObjectId registrationId,
        int year = 2027,
        string? accreditationNumber = null,
        ObjectId? applicationId = null
    ) =>
        new()
        {
            RegistrationId = registrationId,
            Year = year,
            AccreditationNumber = accreditationNumber ?? $"A27EX{Next():D7}P",
            ApplicationId = applicationId,
            Status = AccreditationStatus.Approved,
            ApprovedAt = new DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            PeriodStart = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PeriodEnd = new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            TonnageBand = TonnageBand.UpTo5000,
            SamplingPlanFiles = [File()],
            Signatories =
            [
                new Signatory { FullName = "A Signatory", Email = "signatory@example.invalid" },
            ],
        };

    private static ApplicationFile File() =>
        new()
        {
            FileId = "file-1",
            Filename = "sampling-plan.pdf",
            ContentType = "application/pdf",
            UploadedAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UploadedByUserId = "user-1",
            ScanStatus = FileScanStatus.Clean,
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
            S3Key = "sampling-plans/file-1",
        };

    private static PostalAddress Address() =>
        new()
        {
            Line1 = "1 Test Street",
            TownOrCity = "Testville",
            Postcode = "TE1 1ST",
            Country = "United Kingdom",
        };
}
