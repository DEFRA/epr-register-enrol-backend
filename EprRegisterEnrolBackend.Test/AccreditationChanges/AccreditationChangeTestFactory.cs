using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

// AccreditationApplicationTestFactory (fake application persistence, fake uploads, substituted
// adapters) plus an in-memory change-request store. submitToCaseManagement drives the RA-492
// hand-off flag; inYearChangesEnabled the feature flag that maps the routes at all.
// RA-739: with caseManagement given, the real HttpCaseWorkingApiAdapter replaces the substitute
// and sends its HTTP calls to that handler, which stands in for ManagementBe.
public class AccreditationChangeTestFactory(
    bool submitToCaseManagement = false,
    bool inYearChangesEnabled = true,
    HttpMessageHandler? caseManagement = null
) : AccreditationApplicationTestFactory
{
    public const string CaseManagementUrl = "http://management-be.test";

    public FakeChangeRequestPersistence FakeChangeRequests { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting(FeatureFlags.InYearChangesEnabledKey, inYearChangesEnabled ? "true" : "false");
        builder.UseSetting(
            "AccreditationChanges:SubmitToCaseManagement",
            submitToCaseManagement ? "true" : "false"
        );
        // As in Program, the change-request store exists only with the flag on.
        if (inYearChangesEnabled)
        {
            builder.ConfigureServices(services =>
                services.AddSingleton<IChangeRequestPersistence>(FakeChangeRequests)
            );
        }
        if (caseManagement is not null)
        {
            builder.UseSetting("CaseWorking:Url", CaseManagementUrl);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ICaseWorkingApiAdapter, HttpCaseWorkingApiAdapter>();
                services
                    .AddHttpClient("DefaultClient")
                    .ConfigurePrimaryHttpMessageHandler(() => new NonDisposingHandler(caseManagement));
            });
        }
    }

    // The client factory disposes the handlers it builds; the caller owns the fake.
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing) { }
    }
}
