using EprRegisterEnrolBackend.StubPersistence.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.StubPersistence.Endpoints;

// StubApplicationEndpoints is only mapped in the Development environment (see Program.cs), and
// depends solely on IStubApplicationPersistence — mocking that out lets these endpoint tests run
// without a real MongoDB instance, mirroring AccreditationApplicationTestFactory's approach for
// IAccreditationApplicationPersistence.
public class StubApplicationEndpointsTestFactory : WebApplicationFactory<Program>
{
    public IStubApplicationPersistence MockPersistence { get; } =
        Substitute.For<IStubApplicationPersistence>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(MockPersistence);
        });
    }
}
