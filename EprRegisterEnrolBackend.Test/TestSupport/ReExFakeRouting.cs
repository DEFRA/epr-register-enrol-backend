namespace EprRegisterEnrolBackend.Test.TestSupport;

/// <summary>
/// Which fake ReEx endpoint a request path resolves to. Shared by every fake
/// HttpMessageHandler that stands in for the real ReEx API in tests, so the "route by URL
/// shape" decision is defined once instead of duplicated per handler.
///
/// RA-580-1: only organisations and ORS-A (accreditation-scoped overseas-sites) exist as real
/// call sites now — ORS-R (registration-scoped) was dropped from HttpReExApiAdapter because
/// ReEx's real implementation always returns identical site membership from both, making ORS-R
/// redundant. See HttpReExApiAdapter.cs for the full reasoning.
/// </summary>
internal enum ReExFakeRoute
{
    Organisation,
    AccreditationOverseasSites,
}

internal static class ReExFakeRouting
{
    public static ReExFakeRoute Classify(string absolutePath) =>
        absolutePath.Contains("accreditations") && absolutePath.Contains("overseas-sites")
            ? ReExFakeRoute.AccreditationOverseasSites
            : ReExFakeRoute.Organisation;
}
