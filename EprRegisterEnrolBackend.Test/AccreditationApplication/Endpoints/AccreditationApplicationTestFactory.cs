using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

public class AccreditationApplicationTestFactory : WebApplicationFactory<Program>
{
    public IAccreditationApplicationPersistence MockPersistence { get; } =
        Substitute.For<IAccreditationApplicationPersistence>();

    public IReExApiAdapter MockReExAdapter { get; } =
        Substitute.For<IReExApiAdapter>();

    public ICaseWorkingApiAdapter MockCaseWorkingAdapter { get; } =
        Substitute.For<ICaseWorkingApiAdapter>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(MockPersistence);
            services.AddSingleton(MockReExAdapter);
            services.AddSingleton(MockCaseWorkingAdapter);
        });
    }
}
