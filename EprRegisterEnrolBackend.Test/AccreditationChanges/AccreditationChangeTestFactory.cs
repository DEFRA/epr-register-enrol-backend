using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

// AccreditationApplicationTestFactory (fake application persistence, fake uploads, substituted
// adapters) plus an in-memory change-request store. submitToCaseManagement drives the RA-492
// hand-off flag; inYearChangesEnabled the feature flag that maps the routes at all.
public class AccreditationChangeTestFactory(
    bool submitToCaseManagement = false,
    bool inYearChangesEnabled = true
) : AccreditationApplicationTestFactory
{
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
    }
}
