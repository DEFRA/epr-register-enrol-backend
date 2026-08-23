using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public enum OperatorSection
{
    Prns,
    BusinessPlan,
    SamplingPlan,
    OverseasSites,
    BesEvidence,
}

// Maps CM's closed six-key vocabulary onto the five operator sections (RA-311 §2), and centralises
// the SectionStatus read/write per section so both the inbound query endpoint, the new status
// gate, and the resubmit endpoint share one definition of "which section does this affect".
public static class AccreditationApplicationSections
{
    public const string AuthorityToIssueKey = "authority-to-issue";
    public const string PrnTonnageKey = "prn-tonnage";
    public const string BusinessPlanKey = "business-plan";
    public const string SamplingAndInspectionPlanKey = "sampling-and-inspection-plan";
    public const string BroadlyEquivalentStandardsKey = "broadly-equivalent-standards";
    public const string OverseasReprocessingSitesKey = "overseas-reprocessing-sites";

    public static readonly IReadOnlySet<string> AllCmSectionKeys = new HashSet<string>
    {
        AuthorityToIssueKey,
        PrnTonnageKey,
        BusinessPlanKey,
        SamplingAndInspectionPlanKey,
        BroadlyEquivalentStandardsKey,
        OverseasReprocessingSitesKey,
    };

    // BES evidence / overseas sites only exist for exporter applications (OverseasSites is only
    // ever created when IsExporter is true — see Seed). authority-to-issue/prn-tonnage (Prns)
    // apply to every application regardless of IsExporter.
    public static readonly IReadOnlySet<string> ExporterOnlyCmSectionKeys = new HashSet<string>
    {
        BroadlyEquivalentStandardsKey,
        OverseasReprocessingSitesKey,
    };

    public static bool TryMapCmKeyToSection(string cmKey, out OperatorSection section)
    {
        switch (cmKey)
        {
            case AuthorityToIssueKey:
            case PrnTonnageKey:
                section = OperatorSection.Prns;
                return true;
            case BusinessPlanKey:
                section = OperatorSection.BusinessPlan;
                return true;
            case SamplingAndInspectionPlanKey:
                section = OperatorSection.SamplingPlan;
                return true;
            case BroadlyEquivalentStandardsKey:
                section = OperatorSection.BesEvidence;
                return true;
            case OverseasReprocessingSitesKey:
                section = OperatorSection.OverseasSites;
                return true;
            default:
                section = default;
                return false;
        }
    }

    // Reverse of TryMapCmKeyToSection — Prns collapses two CM keys onto one section, so both are
    // reported back on resubmit since which one(s) were originally raised isn't tracked separately.
    public static IReadOnlyList<string> CmSectionKeysFor(OperatorSection section) =>
        section switch
        {
            OperatorSection.Prns => [AuthorityToIssueKey, PrnTonnageKey],
            OperatorSection.BusinessPlan => [BusinessPlanKey],
            OperatorSection.SamplingPlan => [SamplingAndInspectionPlanKey],
            OperatorSection.BesEvidence => [BroadlyEquivalentStandardsKey],
            OperatorSection.OverseasSites => [OverseasReprocessingSitesKey],
            _ => [],
        };

    public static SectionStatus GetSectionStatus(
        AccreditationApplicationModel application,
        OperatorSection section
    ) =>
        section switch
        {
            OperatorSection.Prns => application.Prns.SectionStatus,
            OperatorSection.BusinessPlan => application.BusinessPlan.SectionStatus,
            OperatorSection.SamplingPlan => application.SamplingPlan.SectionStatus,
            OperatorSection.OverseasSites =>
                application.OverseasSites?.SectionStatus ?? SectionStatus.NotStarted,
            OperatorSection.BesEvidence =>
                application.BesEvidence?.SectionStatus ?? SectionStatus.NotStarted,
            _ => SectionStatus.NotStarted,
        };

    public static void SetSectionStatus(
        AccreditationApplicationModel application,
        OperatorSection section,
        SectionStatus status
    )
    {
        switch (section)
        {
            case OperatorSection.Prns:
                application.Prns.SectionStatus = status;
                break;
            case OperatorSection.BusinessPlan:
                application.BusinessPlan.SectionStatus = status;
                break;
            case OperatorSection.SamplingPlan:
                application.SamplingPlan.SectionStatus = status;
                break;
            case OperatorSection.OverseasSites:
                application.OverseasSites ??= new AccreditationApplicationOverseasSites();
                application.OverseasSites.SectionStatus = status;
                break;
            case OperatorSection.BesEvidence:
                application.BesEvidence ??= new AccreditationApplicationBesEvidence();
                application.BesEvidence.SectionStatus = status;
                break;
        }
    }

    // What the section's status would naturally compute to right now, ignoring any Queried
    // override — used by the resubmit endpoint to reset sections the operator never touched
    // back to a real value instead of leaving them stuck at Queried. Mirrors the same
    // computations each Patch endpoint already applies (SectionStatusService for Prns/
    // BusinessPlan/SamplingPlan, the inline Any(Selected) check for OverseasSites). BesEvidence
    // has no computed state — it's operator-controlled directly via PatchBesEvidenceSection —
    // so an untouched, still-Queried BesEvidence resets to NotStarted.
    public static SectionStatus ComputeCurrentStatus(
        AccreditationApplicationModel application,
        OperatorSection section
    ) =>
        section switch
        {
            OperatorSection.Prns => SectionStatusService.ComputePrns(application.Prns),
            OperatorSection.BusinessPlan => SectionStatusService.ComputeBusinessPlan(
                application.BusinessPlan
            ),
            OperatorSection.SamplingPlan => SectionStatusService.ComputeSamplingPlan(
                application.SamplingPlan
            ),
            OperatorSection.OverseasSites => (application.OverseasSites?.Sites ?? []).Any(s =>
                s.Selected
            )
                ? SectionStatus.Completed
                : SectionStatus.NotStarted,
            OperatorSection.BesEvidence => SectionStatus.NotStarted,
            _ => SectionStatus.NotStarted,
        };

    // Appends a version snapshot of the section's current values — called on Submit (version 1,
    // all 5 sections) and on each resubmit-after-query (the resubmitted sections only). CM's
    // "latest data" need is served by the resubmit push itself; nothing else reads Versions back.
    public static void SnapshotSection(
        AccreditationApplicationModel application,
        OperatorSection section,
        DateTime versionedAt
    )
    {
        switch (section)
        {
            case OperatorSection.Prns:
                application.Prns.Versions.Add(
                    new PrnsSnapshot
                    {
                        PlannedTonnageBand = application.Prns.PlannedTonnageBand,
                        Authorisers = [.. application.Prns.Authorisers],
                        VersionedAt = versionedAt,
                    }
                );
                break;
            case OperatorSection.BusinessPlan:
                var bp = application.BusinessPlan;
                bp.Versions.Add(
                    new BusinessPlanSnapshot
                    {
                        NewInfrastructurePercent = bp.NewInfrastructurePercent,
                        PriceSupportPercent = bp.PriceSupportPercent,
                        BusinessCollectionsPercent = bp.BusinessCollectionsPercent,
                        CommunicationsPercent = bp.CommunicationsPercent,
                        NewMarketsPercent = bp.NewMarketsPercent,
                        NewUsesPercent = bp.NewUsesPercent,
                        OtherPercent = bp.OtherPercent,
                        NewInfrastructureDetail = bp.NewInfrastructureDetail,
                        PriceSupportDetail = bp.PriceSupportDetail,
                        BusinessCollectionsDetail = bp.BusinessCollectionsDetail,
                        CommunicationsDetail = bp.CommunicationsDetail,
                        NewMarketsDetail = bp.NewMarketsDetail,
                        NewUsesDetail = bp.NewUsesDetail,
                        OtherDetail = bp.OtherDetail,
                        VersionedAt = versionedAt,
                    }
                );
                break;
            case OperatorSection.SamplingPlan:
                application.SamplingPlan.Versions.Add(
                    new SamplingPlanSnapshot
                    {
                        Files = [.. application.SamplingPlan.Files],
                        VersionedAt = versionedAt,
                    }
                );
                break;
            case OperatorSection.OverseasSites:
                application.OverseasSites ??= new AccreditationApplicationOverseasSites();
                application.OverseasSites.Versions.Add(
                    new OverseasSitesSnapshot
                    {
                        Sites = [.. application.OverseasSites.Sites],
                        VersionedAt = versionedAt,
                    }
                );
                break;
            case OperatorSection.BesEvidence:
                application.BesEvidence ??= new AccreditationApplicationBesEvidence();
                application.BesEvidence.Versions.Add(
                    new BesEvidenceSnapshot { VersionedAt = versionedAt }
                );
                break;
        }
    }

    // The only new restriction RA-311 adds: while an application is Queried, only the sections
    // CM actually queried may be edited — every other non-terminal application status keeps its
    // existing behaviour completely unchanged. Approved/Rejected/Withdrawn no longer reach this
    // check at all: AccreditationApplicationEndpoints.RejectIfTerminal rejects writes to those
    // three statuses up front (RA-415, closing the RA-311 §9 follow-up), so this method only
    // ever sees a non-terminal status.
    public static bool IsSectionEditable(ApplicationStatus appStatus, SectionStatus sectionStatus) =>
        appStatus != ApplicationStatus.Queried || sectionStatus == SectionStatus.Queried;
}
