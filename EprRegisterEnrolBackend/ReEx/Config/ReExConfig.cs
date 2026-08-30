namespace EprRegisterEnrolBackend.ReEx.Config;

public class ReExConfig
{
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Wires up StubReExApiAdapter/FakeOrganisationPersistence instead of the real
    /// HttpReExApiAdapter, even outside Development. Lets a deployed environment
    /// (e.g. perf-test) run against the in-memory ReEx fixtures without needing
    /// ASPNETCORE_ENVIRONMENT=Development, which would also enable unrelated
    /// dev-only surface area (stub application endpoints, DevScanAutoCompleteService).
    /// Defaults false so prod/dev/test/ext-test behave exactly as before this flag
    /// existed, unaffected by this default.
    /// </summary>
    public bool UseStub { get; set; }
}
