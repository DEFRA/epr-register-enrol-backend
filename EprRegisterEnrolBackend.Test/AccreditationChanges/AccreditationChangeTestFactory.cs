using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

// AccreditationApplicationTestFactory (fake application persistence, fake uploads, substituted
// adapters) plus an in-memory change-request store. submitToCaseManagement drives the RA-492
// hand-off flag.
public class AccreditationChangeTestFactory(bool submitToCaseManagement = false)
    : AccreditationApplicationTestFactory
{
    public FakeChangeRequestPersistence FakeChangeRequests { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting(
            "AccreditationChanges:SubmitToCaseManagement",
            submitToCaseManagement ? "true" : "false"
        );
        builder.ConfigureServices(services =>
            services.AddSingleton<IChangeRequestPersistence>(FakeChangeRequests)
        );
    }
}
