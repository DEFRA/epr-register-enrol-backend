using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using MongoDB.Bson;
using LegacyQuerySubmission = EprRegisterEnrolBackend.AccreditationApplication.Models.QuerySubmission;

namespace EprRegisterEnrolBackend.Test.DomainModel.Migration;

public class LegacyApplicationMapperTests
{
    private static readonly DateTime FixedDate = new(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly ObjectId RegistrationId = ObjectId.GenerateNewId();

    private static AccreditationApplicationModel Legacy() =>
        new()
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-1",
            RegistrationId = "reg-1",
            MaterialType = MaterialType.Plastic,
            Year = 2027,
        };

    private static InterimSiteModel Interim() =>
        new()
        {
            SiteId = 3,
            SiteNumber = "001",
            Country = "Belgium",
            SiteName = "Interim",
            AddressLine1 = "1 Quai",
            TownOrCity = "Antwerp",
            ContactName = "Contact",
            ContactEmail = "contact@example.com",
            ContactPhone = "000",
        };

    [Fact]
    public void ToEntity_CopiesTheApplicationsIdentityAndCaseFields()
    {
        var legacy = Legacy();
        legacy.ApplicationStatus = ApplicationStatus.Queried;
        legacy.ApplicationReference = "ref-1";
        legacy.PaymentReference = "pay-1";
        legacy.CaseManagementWorkItemId = Guid.NewGuid();
        legacy.CaseManagementStatusUpdatedAt = FixedDate;
        legacy.WithdrawalReason = "reason";
        legacy.DateSent = FixedDate.AddDays(-1);
        legacy.DateLastEdited = FixedDate;
        legacy.AccreditationReference = "A27EX1234567P";
        legacy.SubmittedBy = new SubmittedByModel
        {
            FullName = "Test User",
            JobTitle = "Manager",
            Email = "test@example.com",
        };
        List<ApplicationOverseasSiteEntry> overseasSites = [];

        var entity = LegacyApplicationMapper.ToEntity(legacy, RegistrationId, overseasSites);

        Assert.Equal(legacy.Id, entity.EntityId);
        Assert.Equal(RegistrationId, entity.RegistrationId);
        Assert.Equal("org-1", entity.ReExOrganisationId);
        Assert.Equal("reg-1", entity.ReExRegistrationId);
        Assert.Equal(2027, entity.Year);
        Assert.Equal(ApplicationStatus.Queried, entity.Status);
        Assert.Equal("ref-1", entity.ApplicationReference);
        Assert.Equal("pay-1", entity.PaymentReference);
        Assert.Equal(legacy.CaseManagementWorkItemId, entity.CaseManagementWorkItemId);
        Assert.Equal(FixedDate, entity.CaseManagementStatusUpdatedAt);
        Assert.Equal("reason", entity.WithdrawalReason);
        Assert.Equal(legacy.DateSent, entity.DateSent);
        Assert.Equal(FixedDate, entity.DateLastEdited);
        Assert.Equal("A27EX1234567P", entity.ReservedAccreditationNumber);
        Assert.Same(overseasSites, entity.ApplicationOverseasSites);
        Assert.Equal("Test User", entity.SubmittedBy!.FullName);
        Assert.Equal("Manager", entity.SubmittedBy.JobTitle);
        Assert.Equal("test@example.com", entity.SubmittedBy.Email);
    }

    [Fact]
    public void ToEntity_ApplicationWithoutAnId_Throws()
    {
        var legacy = Legacy();
        legacy.Id = null;

        Assert.Throws<ArgumentException>(() =>
            LegacyApplicationMapper.ToEntity(legacy, RegistrationId, [])
        );
    }

    [Fact]
    public void ToEntity_ConvertsThePrnsSectionAndItsSnapshots()
    {
        var legacy = Legacy();
        legacy.Prns = new AccreditationApplicationPrns
        {
            PlannedTonnageBand = PlannedTonnageBand.UpTo10000,
            Authorisers =
            [
                new PrnsAuthoriser
                {
                    FullName = "A",
                    Email = "a@example.com",
                    IsNew = true,
                },
            ],
            SectionStatus = SectionStatus.Completed,
            Versions =
            [
                new PrnsSnapshot
                {
                    PlannedTonnageBand = PlannedTonnageBand.UpTo500,
                    VersionedAt = FixedDate,
                },
            ],
        };

        var prns = LegacyApplicationMapper.ToEntity(legacy, RegistrationId, []).Prns;

        Assert.Equal(TonnageBand.UpTo10000, prns.PlannedTonnageBand);
        Assert.Equal(SectionStatus.Completed, prns.SectionStatus);
        var authoriser = Assert.Single(prns.Authorisers);
        Assert.Equal("A", authoriser.FullName);
        Assert.Equal("a@example.com", authoriser.Email);
        Assert.True(authoriser.IsNew);
        var snapshot = Assert.Single(prns.Versions);
        Assert.Equal(TonnageBand.UpTo500, snapshot.PlannedTonnageBand);
        Assert.Equal(FixedDate, snapshot.VersionedAt);
    }

    [Fact]
    public void ToEntity_CopiesTheBusinessPlanAndSamplingPlanWithTheirSnapshots()
    {
        var legacy = Legacy();
        legacy.BusinessPlan = new AccreditationApplicationBusinessPlan
        {
            NewInfrastructurePercent = 40,
            OtherDetail = "x",
            SectionStatus = SectionStatus.InProgress,
            Versions = [new BusinessPlanSnapshot { NewUsesPercent = 10, VersionedAt = FixedDate }],
        };
        var file = new AccreditationApplicationFile
        {
            FileId = "f1",
            Filename = "plan.pdf",
            ContentType = "application/pdf",
            UploadedByUserId = "u1",
            ScanStatus = FileScanStatus.Clean,
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
            S3Key = "k1",
        };
        legacy.SamplingPlan = new AccreditationApplicationSamplingPlan
        {
            Files = [file],
            SectionStatus = SectionStatus.Completed,
            Versions = [new SamplingPlanSnapshot { Files = [file], VersionedAt = FixedDate }],
        };

        var entity = LegacyApplicationMapper.ToEntity(legacy, RegistrationId, []);

        Assert.Equal(40, entity.BusinessPlan.NewInfrastructurePercent);
        Assert.Equal("x", entity.BusinessPlan.OtherDetail);
        Assert.Equal(SectionStatus.InProgress, entity.BusinessPlan.SectionStatus);
        Assert.Equal(10, Assert.Single(entity.BusinessPlan.Versions).NewUsesPercent);

        var mapped = Assert.Single(entity.SamplingPlan.Files);
        Assert.Equal("f1", mapped.FileId);
        Assert.Equal("plan.pdf", mapped.Filename);
        Assert.Equal("application/pdf", mapped.ContentType);
        Assert.Equal("u1", mapped.UploadedByUserId);
        Assert.Equal(FileScanStatus.Clean, mapped.ScanStatus);
        Assert.Equal(AccreditationFileDocumentType.SamplingPlan, mapped.DocumentType);
        Assert.Equal("k1", mapped.S3Key);
        var snapshot = Assert.Single(entity.SamplingPlan.Versions);
        Assert.Equal("f1", Assert.Single(snapshot.Files).FileId);
        Assert.Equal(FixedDate, snapshot.VersionedAt);
    }

    [Fact]
    public void ToEntity_MapsTheQueryAndItsSubmissions()
    {
        var legacy = Legacy();
        legacy.Query = new AccreditationApplicationQuery
        {
            QueryNote = "Test Note",
            QueriedSectionKeys = ["prns"],
            QuerySubmissions =
            [
                new LegacyQuerySubmission
                {
                    QuerySubmissionTime = FixedDate,
                    SectionKeys = ["prns"],
                    QuerySubmitterContactDetails = new QuerySubmitterContactDetails
                    {
                        FullName = "Submitter",
                        Email = "sub@example.com",
                        Role = "Admin",
                    },
                },
            ],
        };

        var query = LegacyApplicationMapper.ToEntity(legacy, RegistrationId, []).Query!;

        Assert.Equal("Test Note", query.QueryNote);
        Assert.Equal(["prns"], query.QueriedSectionKeys);
        Assert.NotSame(legacy.Query.QueriedSectionKeys, query.QueriedSectionKeys);
        var submission = Assert.Single(query.QuerySubmissions);
        Assert.Equal(FixedDate, submission.QuerySubmissionTime);
        Assert.Equal(["prns"], submission.SectionKeys);
        Assert.Equal("Submitter", submission.QuerySubmitterContactDetails.FullName);
        Assert.Equal("sub@example.com", submission.QuerySubmitterContactDetails.Email);
        Assert.Equal("Admin", submission.QuerySubmitterContactDetails.Role);
    }

    [Fact]
    public void ToEntity_NullOptionalSections_StayNull()
    {
        var legacy = Legacy();

        var entity = LegacyApplicationMapper.ToEntity(legacy, null, []);

        Assert.Null(entity.RegistrationId);
        Assert.Null(entity.Query);
        Assert.Null(entity.BesEvidence);
        Assert.Null(entity.OverseasSitesSection);
        Assert.Null(entity.SubmittedBy);
    }

    [Fact]
    public void ToEntity_KeepsTheOverseasSitesSubmissionHistory()
    {
        var interim = Interim();
        var legacy = Legacy();
        legacy.OverseasSites = new AccreditationApplicationOverseasSites
        {
            SectionStatus = SectionStatus.Completed,
            Versions =
            [
                new OverseasSitesSnapshot
                {
                    VersionedAt = FixedDate,
                    Sites =
                    [
                        new OverseasSiteModel
                        {
                            SiteId = 1,
                            OrsId = "001",
                            SiteName = "S",
                            IsNewSite = true,
                            RegisteredNowAccredited = true,
                            InterimSite = interim,
                            InterimSites = [interim],
                            PreviousSites =
                            [
                                new OverseasSiteModel { SiteId = 2, SiteName = "Old" },
                            ],
                        },
                    ],
                },
            ],
        };

        var section = LegacyApplicationMapper
            .ToEntity(legacy, RegistrationId, [])
            .OverseasSitesSection!;

        Assert.Equal(SectionStatus.Completed, section.SectionStatus);
        var snapshot = Assert.Single(section.Versions);
        Assert.Equal(FixedDate, snapshot.VersionedAt);
        var site = Assert.Single(snapshot.Sites);
        Assert.Equal("001", site.OrsId);
        Assert.True(site.IsNewSite);
        Assert.True(site.RegisteredNowAccredited);
        Assert.Equal("001", site.InterimSite!.SiteNumber);
        Assert.Equal(3, Assert.Single(site.InterimSites).SiteId);
        Assert.Equal("Old", Assert.Single(site.PreviousSites).SiteName);
    }

    [Fact]
    public void ToBesEvidence_Null_IsNull() =>
        Assert.Null(LegacyApplicationMapper.ToBesEvidence(null));

    [Fact]
    public void ToBesEvidence_CopiesEveryUpload()
    {
        var legacy = new BesEvidenceModel
        {
            DoYouWantToUploadMoreEvidence = false,
            BesEvidenceUploads =
            [
                new BesEvidenceFileModel
                {
                    FileId = "file-1",
                    Filename = "doc.pdf",
                    ContentType = "application/pdf",
                    ScanStatus = "Clean",
                    BesEvidenceValidFromDate = "2027-01-01",
                    BesEvidenceExpiryDate = "2028-01-01",
                    UploadedAt = FixedDate,
                    UploadedBy = "user-1",
                    S3Key = "s3-key",
                    S3Bucket = "bucket-1",
                },
            ],
        };

        var result = LegacyApplicationMapper.ToBesEvidence(legacy)!;

        Assert.False(result.DoYouWantToUploadMoreEvidence);
        var file = Assert.Single(result.BesEvidenceUploads);
        Assert.Equal("file-1", file.FileId);
        Assert.Equal("doc.pdf", file.Filename);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("Clean", file.ScanStatus);
        Assert.Equal("2027-01-01", file.BesEvidenceValidFromDate);
        Assert.Equal("2028-01-01", file.BesEvidenceExpiryDate);
        Assert.Equal(FixedDate, file.UploadedAt);
        Assert.Equal("user-1", file.UploadedBy);
        Assert.Equal("s3-key", file.S3Key);
        Assert.Equal("bucket-1", file.S3Bucket);
    }
}
