using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.StubPersistence.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

            // MongoIndexInitializerService resolves these at startup; substitute
            // them so it does not construct the real MongoService-backed
            // implementations (and block on a server-selection timeout) in a
            // suite that has no Mongo.
            services.RemoveAll<IAccreditationApplicationPersistence>();
            services.AddSingleton(Substitute.For<IAccreditationApplicationPersistence>());
            services.RemoveAll<IRecyclingOperationsAuditPersistence>();
            services.AddSingleton(Substitute.For<IRecyclingOperationsAuditPersistence>());
            services.RemoveAll<IPendingUploadService>();
            services.AddSingleton(Substitute.For<IPendingUploadService>());
            services.RemoveAll<ICaseManagementAuthNonceStore>();
            services.AddSingleton(Substitute.For<ICaseManagementAuthNonceStore>());
        });
    }
}
