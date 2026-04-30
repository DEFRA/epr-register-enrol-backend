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
        var allSet = bp.NewInfrastructurePercent.HasValue &&
                     bp.PriceSupportPercent.HasValue &&
                     bp.BusinessCollectionsPercent.HasValue &&
                     bp.CommunicationsPercent.HasValue &&
                     bp.NewMarketsPercent.HasValue &&
                     bp.NewUsesPercent.HasValue;

        if (!allSet)
        {
            var anySet = bp.NewInfrastructurePercent.HasValue ||
                         bp.PriceSupportPercent.HasValue ||
                         bp.BusinessCollectionsPercent.HasValue ||
                         bp.CommunicationsPercent.HasValue ||
                         bp.NewMarketsPercent.HasValue ||
                         bp.NewUsesPercent.HasValue;

            return anySet ? SectionStatus.InProgress : SectionStatus.NotStarted;
        }

        var sum = (bp.NewInfrastructurePercent ?? 0) +
                  (bp.PriceSupportPercent ?? 0) +
                  (bp.BusinessCollectionsPercent ?? 0) +
                  (bp.CommunicationsPercent ?? 0) +
                  (bp.NewMarketsPercent ?? 0) +
                  (bp.NewUsesPercent ?? 0);

        return sum == 100 ? SectionStatus.Completed : SectionStatus.InProgress;
    }

    public static SectionStatus ComputeSamplingPlan(AccreditationApplicationSamplingPlan samplingPlan)
    {
        if (samplingPlan.Files.Count == 0)
            return SectionStatus.NotStarted;

        return samplingPlan.Files.Any(f => f.ScanStatus == FileScanStatus.Clean)
            ? SectionStatus.Completed
            : SectionStatus.InProgress;
    }
}
