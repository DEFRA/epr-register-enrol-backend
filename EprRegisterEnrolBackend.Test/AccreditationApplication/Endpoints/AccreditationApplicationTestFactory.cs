using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

public class AccreditationApplicationTestFactory : WebApplicationFactory<Program>
{
    public FakeAccreditationApplicationPersistence FakePersistence { get; } = new();

    // RA-448: deterministic in-memory counter store, so registration/accreditation
    // number tests can assert exact sequence values without a real Mongo instance.
    public FakeRegulatoryNumberSequenceCounterPersistence FakeCounters { get; } = new();

    public IReExApiAdapter MockReExAdapter { get; } = Substitute.For<IReExApiAdapter>();
    public ICaseWorkingApiAdapter MockCaseWorkingAdapter { get; } =
        Substitute.For<ICaseWorkingApiAdapter>();
    public ICdpUploaderService MockCdpUploaderService { get; } =
        Substitute.For<ICdpUploaderService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IAccreditationApplicationPersistence>(FakePersistence);
            services.AddSingleton<IRegulatoryNumberSequenceCounterPersistence>(FakeCounters);
            services.AddSingleton(MockReExAdapter);
            services.AddSingleton(MockCaseWorkingAdapter);
            services.AddSingleton(MockCdpUploaderService);
        });
    }
}
