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

    // RA-496: resolves what SectionStatus a Patch endpoint should persist given the client's
    // requested intent ("save and come back later" -> InProgress, "save and continue" ->
    // Completed). The requested status is an intent, not an unconditional command — Completed is
    // only honoured when the section is actually complete per the same `compute` this service
    // already uses for the legacy auto-computed status, closing the trust-boundary gap a client
    // would otherwise have to just POST Completed with incomplete data. When the client sends no
    // explicit status at all (older callers, or the field omitted), falls back to that legacy
    // auto-computed behaviour so existing integrations are unaffected. Callers are expected to
    // skip this entirely while the section is Queried — that guard stays at the call site
    // alongside each section's existing `!= Queried` check.
    public static (SectionStatus? Status, string? Error) ResolveRequestedStatus(
        SectionStatus? requestedStatus,
        Func<SectionStatus> compute,
        string sectionDisplayName
    )
    {
        if (!requestedStatus.HasValue)
            return (compute(), null);

        if (requestedStatus.Value == SectionStatus.Queried)
            return (null, $"{sectionDisplayName} section status cannot be set to Queried directly.");

        if (requestedStatus.Value == SectionStatus.Completed && compute() != SectionStatus.Completed)
            return (
                null,
                $"{sectionDisplayName} section cannot be marked Completed until it is complete."
            );

        return (requestedStatus.Value, null);
    }

    // BES evidence has no NotStarted/InProgress/Completed auto-compute — SectionStatus there is
    // operator-controlled directly via PatchBesEvidenceSection (see
    // AccreditationApplicationSections.ComputeCurrentStatus). This answers only the completeness
    // half of the same Completed gate ResolveRequestedStatus applies elsewhere: every selected
    // overseas site that needs evidence (not EU/OECD, no conditions-of-export exemption) must
    // have at least one uploaded file. Vacuously true when there are no such sites, so an
    // exporter with only EU/OECD sites can still complete the section.
    public static bool IsBesEvidenceComplete(AccreditationApplicationOverseasSites? overseasSites)
    {
        var sites = overseasSites?.Sites ?? [];
        return sites
            .Where(s => s.Selected && !s.IsEu && !s.IsOecd && s.ConditionsOfExport != true)
            .All(s => (s.BesEvidence?.BesEvidenceUploads.Count ?? 0) > 0);
    }
}
