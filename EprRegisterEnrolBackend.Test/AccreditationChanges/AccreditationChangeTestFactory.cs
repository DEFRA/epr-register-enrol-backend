using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    // RA-000: so a test can assert the Error log a blocked submit writes.
    public CapturingLoggerFactory LogCapture { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddSingleton<ILoggerFactory>(LogCapture));
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
