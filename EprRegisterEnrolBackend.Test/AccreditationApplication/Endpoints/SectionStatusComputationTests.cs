using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

public class SectionStatusComputationTests
{
    [Fact]
    public void Prns_NothingSet_IsNotStarted()
    {
        var prns = new AccreditationApplicationPrns();
        SectionStatusService.ComputePrns(prns).Should().Be(SectionStatus.NotStarted);
    }

    [Fact]
    public void Prns_TonnageBandOnly_IsInProgress()
    {
        var prns = new AccreditationApplicationPrns { PlannedTonnageBand = PlannedTonnageBand.UpTo500 };
        SectionStatusService.ComputePrns(prns).Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public void Prns_AuthorisersOnly_IsInProgress()
    {
        var prns = new AccreditationApplicationPrns
        {
            Authorisers = [new PrnsAuthoriser { FullName = "Jane", Email = "jane@x.com" }]
        };
        SectionStatusService.ComputePrns(prns).Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public void Prns_TonnageBandAndAuthorisers_IsCompleted()
    {
        var prns = new AccreditationApplicationPrns
        {
            PlannedTonnageBand = PlannedTonnageBand.UpTo1000,
            Authorisers = [new PrnsAuthoriser { FullName = "Jane", Email = "jane@x.com" }]
        };
        SectionStatusService.ComputePrns(prns).Should().Be(SectionStatus.Completed);
    }

    // --- Business Plan ---

    [Fact]
    public void BusinessPlan_NothingSet_IsNotStarted()
    {
        var bp = new AccreditationApplicationBusinessPlan();
        SectionStatusService.ComputeBusinessPlan(bp).Should().Be(SectionStatus.NotStarted);
    }

    [Fact]
    public void BusinessPlan_PartialPercents_IsInProgress()
    {
        var bp = new AccreditationApplicationBusinessPlan { NewInfrastructurePercent = 50 };
        SectionStatusService.ComputeBusinessPlan(bp).Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public void BusinessPlan_AllSetSumTo100_IsCompleted()
    {
        var bp = new AccreditationApplicationBusinessPlan
        {
            NewInfrastructurePercent = 20,
            PriceSupportPercent = 20,
            BusinessCollectionsPercent = 20,
            CommunicationsPercent = 20,
            NewMarketsPercent = 10,
            NewUsesPercent = 10
        };
        SectionStatusService.ComputeBusinessPlan(bp).Should().Be(SectionStatus.Completed);
    }

    [Fact]
    public void BusinessPlan_AllSetButSumNot100_IsInProgress()
    {
        var bp = new AccreditationApplicationBusinessPlan
        {
            NewInfrastructurePercent = 10,
            PriceSupportPercent = 10,
            BusinessCollectionsPercent = 10,
            CommunicationsPercent = 10,
            NewMarketsPercent = 10,
            NewUsesPercent = 10
        };
        SectionStatusService.ComputeBusinessPlan(bp).Should().Be(SectionStatus.InProgress);
    }

    // --- Sampling Plan ---

    [Fact]
    public void SamplingPlan_NoFiles_IsNotStarted()
    {
        var sp = new AccreditationApplicationSamplingPlan();
        SectionStatusService.ComputeSamplingPlan(sp).Should().Be(SectionStatus.NotStarted);
    }

    [Fact]
    public void SamplingPlan_PendingFilesOnly_IsInProgress()
    {
        var sp = new AccreditationApplicationSamplingPlan
        {
            Files =
            [
                new AccreditationApplicationFile
                {
                    FileId = "f1", Filename = "plan.pdf", ContentType = "application/pdf",
                    UploadedByUserId = "u1", ScanStatus = FileScanStatus.Pending, S3Key = "sampling-plans/f1"
                }
            ]
        };
        SectionStatusService.ComputeSamplingPlan(sp).Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public void SamplingPlan_CleanFile_IsCompleted()
    {
        var sp = new AccreditationApplicationSamplingPlan
        {
            Files =
            [
                new AccreditationApplicationFile
                {
                    FileId = "f1", Filename = "plan.pdf", ContentType = "application/pdf",
                    UploadedByUserId = "u1", ScanStatus = FileScanStatus.Clean, S3Key = "sampling-plans/f1"
                }
            ]
        };
        SectionStatusService.ComputeSamplingPlan(sp).Should().Be(SectionStatus.Completed);
    }

    [Fact]
    public void SamplingPlan_InfectedFilesOnly_IsInProgress()
    {
        var sp = new AccreditationApplicationSamplingPlan
        {
            Files =
            [
                new AccreditationApplicationFile
                {
                    FileId = "f1", Filename = "bad.pdf", ContentType = "application/pdf",
                    UploadedByUserId = "u1", ScanStatus = FileScanStatus.Infected, S3Key = "sampling-plans/f1"
                }
            ]
        };
        SectionStatusService.ComputeSamplingPlan(sp).Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public void SamplingPlan_MixedCleanAndInfected_IsInProgress()
    {
        var sp = new AccreditationApplicationSamplingPlan
        {
            Files =
            [
                new AccreditationApplicationFile
                {
                    FileId = "f1", Filename = "good.pdf", ContentType = "application/pdf",
                    UploadedByUserId = "u1", ScanStatus = FileScanStatus.Clean, S3Key = "sampling-plans/f1"
                },
                new AccreditationApplicationFile
                {
                    FileId = "f2", Filename = "bad.pdf", ContentType = "application/pdf",
                    UploadedByUserId = "u1", ScanStatus = FileScanStatus.Infected, S3Key = "sampling-plans/f2"
                }
            ]
        };
        SectionStatusService.ComputeSamplingPlan(sp).Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public void SamplingPlan_MixedCleanAndPending_IsInProgress()
    {
        var sp = new AccreditationApplicationSamplingPlan
        {
            Files =
            [
                new AccreditationApplicationFile
                {
                    FileId = "f1", Filename = "good.pdf", ContentType = "application/pdf",
                    UploadedByUserId = "u1", ScanStatus = FileScanStatus.Clean, S3Key = "sampling-plans/f1"
                },
                new AccreditationApplicationFile
                {
                    FileId = "f2", Filename = "pending.pdf", ContentType = "application/pdf",
                    UploadedByUserId = "u1", ScanStatus = FileScanStatus.Pending, S3Key = "sampling-plans/f2"
                }
            ]
        };
        SectionStatusService.ComputeSamplingPlan(sp).Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public void SamplingPlan_AllClean_IsCompleted()
    {
        var sp = new AccreditationApplicationSamplingPlan
        {
            Files =
            [
                new AccreditationApplicationFile
                {
                    FileId = "f1", Filename = "doc1.pdf", ContentType = "application/pdf",
                    UploadedByUserId = "u1", ScanStatus = FileScanStatus.Clean, S3Key = "sampling-plans/f1"
                },
                new AccreditationApplicationFile
                {
                    FileId = "f2", Filename = "doc2.pdf", ContentType = "application/pdf",
                    UploadedByUserId = "u1", ScanStatus = FileScanStatus.Clean, S3Key = "sampling-plans/f2"
                }
            ]
        };
        SectionStatusService.ComputeSamplingPlan(sp).Should().Be(SectionStatus.Completed);
    }
}
