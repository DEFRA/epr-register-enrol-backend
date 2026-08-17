using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// Computes the accreditation charge the operator was shown on the payment-details page.
///
/// RA-316: the charge is sent to ManagementBe so Case Management can DISPLAY the identical
/// number the operator saw when duly making, rather than recomputing it from the same inputs
/// and drifting. This is a display/echo value, not an authoritative billing figure.
/// </summary>
///
/// <remarks>
/// DUPLICATED CONSTANTS — DO NOT LET THESE DRIFT.
///
/// The fee table and the per-site fee below are a deliberate mirror of the legacy frontend
/// helper <c>epr-register-enrol-frontend/src/server/common/helpers/paymentDetails.js</c>
/// (<c>TONNAGE_FEES</c>, <c>ORS_FEE</c> and <c>buildPaymentDetails</c>). The frontend computes
/// the same charge purely for display; this backend now computes it again to forward it.
/// Two copies of a money table is a known wart — a follow-up is filed to unify them behind a
/// single source of truth. Until then, ANY change to the fees on either side MUST be applied
/// to the other in the same change set, or Case Management will show the operator one number
/// and the regulator another.
/// </remarks>
internal static class AccreditationChargeCalculator
{
    /// <summary>Fee in whole pounds for each planned tonnage band.</summary>
    /// <remarks>Mirrors <c>TONNAGE_FEES</c> in the legacy frontend's paymentDetails.js.</remarks>
    internal static readonly IReadOnlyDictionary<
        PlannedTonnageBand,
        int
    > TonnageFeesPounds = new Dictionary<PlannedTonnageBand, int>
    {
        [PlannedTonnageBand.UpTo500] = 546,
        [PlannedTonnageBand.UpTo5000] = 2184,
        [PlannedTonnageBand.UpTo10000] = 3276,
        [PlannedTonnageBand.Over10000] = 3965,
    };

    /// <summary>Fee in whole pounds per selected overseas reprocessing site.</summary>
    /// <remarks>Mirrors <c>ORS_FEE</c> in the legacy frontend's paymentDetails.js.</remarks>
    internal const int OverseasSiteFeePounds = 328;

    private const int PencePerPound = 100;

    /// <summary>
    /// Charge in pence (minor units), or <c>null</c> when it cannot be determined.
    /// </summary>
    /// <remarks>
    /// Returns null — rather than throwing, as the frontend's <c>tonnageFeeCalculator</c> does —
    /// when the planned tonnage band is unset or is a value with no fee in the table. The
    /// frontend can afford to throw because it is rendering one page; here the same throw would
    /// take down the whole submission to ManagementBe over a field that is only along for the
    /// ride. Callers omit the field entirely in that case (see JsonOptions' WhenWritingNull), so
    /// Case Management shows "no charge captured" rather than a wrong number. Deliberately NOT
    /// falling back to a sites-only total: the tonnage fee dominates the charge, so a partial
    /// figure would understate it and read as authoritative.
    /// </remarks>
    internal static int? CalculateChargePence(AccreditationApplicationModel application)
    {
        ArgumentNullException.ThrowIfNull(application);

        return CalculateChargePence(
            application.Prns.PlannedTonnageBand,
            CountSelectedOverseasSites(application)
        );
    }

    internal static int? CalculateChargePence(PlannedTonnageBand? band, int selectedOverseasSites)
    {
        if (band is not { } value || !TonnageFeesPounds.TryGetValue(value, out var tonnageFee))
        {
            return null;
        }

        var pounds = tonnageFee + (OverseasSiteFeePounds * selectedOverseasSites);
        return pounds * PencePerPound;
    }

    /// <summary>
    /// Counts overseas sites that count towards the charge.
    /// </summary>
    /// <remarks>
    /// The frontend filters <c>s.selected !== false</c> — i.e. a site with no stored flag counts.
    /// <see cref="OverseasSiteModel.Selected" /> is a non-nullable bool defaulting to
    /// <c>true</c>, so testing it directly is the exact equivalent: an absent value deserialises
    /// to the same default the frontend's <c>!== false</c> lets through.
    /// </remarks>
    internal static int CountSelectedOverseasSites(AccreditationApplicationModel application) =>
        (application.OverseasSites?.Sites ?? []).Count(site => site.Selected);
}
