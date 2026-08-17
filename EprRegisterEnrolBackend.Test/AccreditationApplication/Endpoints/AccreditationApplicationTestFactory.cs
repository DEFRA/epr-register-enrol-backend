using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.CdpUploader.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

public class AccreditationApplicationTestFactory : WebApplicationFactory<Program>
{
    public FakeAccreditationApplicationPersistence FakePersistence { get; } = new();

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
            services.AddSingleton(MockReExAdapter);
            services.AddSingleton(MockCaseWorkingAdapter);
            services.AddSingleton(MockCdpUploaderService);
        });
    }
}
