namespace EprRegisterEnrolBackend.Test.TestSupport;

/// <summary>
/// Which fake ReEx endpoint a request path resolves to. Shared by every fake
/// HttpMessageHandler that stands in for the real ReEx API in tests, so the "route by URL
/// shape" decision — organisations vs. ORS-R (registration-scoped overseas-sites, no
/// "accreditations" segment) vs. ORS-A (accreditation-scoped overseas-sites) — is defined
/// once instead of duplicated per handler.
/// </summary>
internal enum ReExFakeRoute
{
    Organisation,
    RegistrationOverseasSites,
    AccreditationOverseasSites,
}

internal static class ReExFakeRouting
{
    public static ReExFakeRoute Classify(string absolutePath)
    {
        if (absolutePath.Contains("accreditations") && absolutePath.Contains("overseas-sites"))
            return ReExFakeRoute.AccreditationOverseasSites;

        return absolutePath.Contains("overseas-sites")
            ? ReExFakeRoute.RegistrationOverseasSites
            : ReExFakeRoute.Organisation;
    }
}
