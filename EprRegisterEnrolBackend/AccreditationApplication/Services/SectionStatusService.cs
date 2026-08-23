using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public static class SectionStatusService
{
    public static SectionStatus ComputePrns(AccreditationApplicationPrns prns)
    {
        if (prns.PlannedTonnageBand.HasValue && prns.Authorisers.Count > 0)
            return SectionStatus.Completed;

        if (prns.PlannedTonnageBand.HasValue || prns.Authorisers.Count > 0)
            return SectionStatus.InProgress;

        return SectionStatus.NotStarted;
    }

    public static SectionStatus ComputeBusinessPlan(AccreditationApplicationBusinessPlan bp)
    {
        var allSet =
            bp.NewInfrastructurePercent.HasValue
            && bp.PriceSupportPercent.HasValue
            && bp.BusinessCollectionsPercent.HasValue
            && bp.CommunicationsPercent.HasValue
            && bp.NewMarketsPercent.HasValue
            && bp.NewUsesPercent.HasValue
            && bp.OtherPercent.HasValue;

        if (!allSet)
        {
            var anySet =
                bp.NewInfrastructurePercent.HasValue
                || bp.PriceSupportPercent.HasValue
                || bp.BusinessCollectionsPercent.HasValue
                || bp.CommunicationsPercent.HasValue
                || bp.NewMarketsPercent.HasValue
                || bp.NewUsesPercent.HasValue
                || bp.OtherPercent.HasValue;

            return anySet ? SectionStatus.InProgress : SectionStatus.NotStarted;
        }

        var sum =
            (bp.NewInfrastructurePercent ?? 0)
            + (bp.PriceSupportPercent ?? 0)
            + (bp.BusinessCollectionsPercent ?? 0)
            + (bp.CommunicationsPercent ?? 0)
            + (bp.NewMarketsPercent ?? 0)
            + (bp.NewUsesPercent ?? 0)
            + (bp.OtherPercent ?? 0);

        return sum == 100 ? SectionStatus.Completed : SectionStatus.InProgress;
    }

    public static SectionStatus ComputeSamplingPlan(
        AccreditationApplicationSamplingPlan samplingPlan
    )
    {
        // Legacy files predate DocumentType and have no value set — treat them as
        // sampling plan files (that was the only kind this endpoint accepted before
        // SupportingEvidence existed) so already-submitted applications don't regress.
        var planFiles = samplingPlan
            .Files.Where(f => f.DocumentType != AccreditationFileDocumentType.SupportingEvidence)
            .ToList();

        if (planFiles.Count == 0)
            return SectionStatus.NotStarted;

        if (planFiles.Any(f => f.ScanStatus == FileScanStatus.Infected))
            return SectionStatus.InProgress;

        if (planFiles.All(f => f.ScanStatus == FileScanStatus.Clean))
            return SectionStatus.Completed;

        return SectionStatus.InProgress;
    }
}
