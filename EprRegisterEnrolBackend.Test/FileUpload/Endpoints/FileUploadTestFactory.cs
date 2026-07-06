using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.FileUpload.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.FileUpload.Endpoints;

public class FileUploadTestFactory : WebApplicationFactory<Program>
{
    public FakeFileUploadPersistence FakePersistence { get; } = new();
    public IS3Service MockS3Service { get; } = Substitute.For<IS3Service>();
    public ICdpUploaderService MockCdpUploaderService { get; } =
        Substitute.For<ICdpUploaderService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IFileUploadPersistence>(FakePersistence);
            services.AddSingleton(MockS3Service);
            services.AddSingleton(MockCdpUploaderService);
            services.AddSingleton(Substitute.For<IReExApiAdapter>());
            services.AddSingleton(Substitute.For<ICaseWorkingApiAdapter>());
        });
    }
}
