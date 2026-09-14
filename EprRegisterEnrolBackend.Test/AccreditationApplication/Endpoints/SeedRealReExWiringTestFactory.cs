using System.Net;
using System.Text;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Test.Auth;
using EprRegisterEnrolBackend.Test.CdpUploader;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// Companion to AccreditationApplicationTestFactory, for the one seam that factory
/// deliberately doesn't cover: it mocks IReExApiAdapter wholesale, so none of its Seed_*
/// tests ever exercise the real HttpReExApiAdapter/ReExClient mapping and merge logic
/// against actual ReEx JSON. This factory wires the REAL HttpReExApiAdapter/ReExClient and
/// only fakes the underlying HTTP transport (FakeReExHandler), so a test hitting the real
/// POST .../seed endpoint here proves the whole path — fake ReEx JSON in, through the real
/// HTTP client and adapter mapping/merge, through the Seed handler, out as the final
/// AccreditationApplicationModel — actually holds together.
/// </summary>
public class SeedRealReExWiringTestFactory : WebApplicationFactory<Program>
{
    public FakeAccreditationApplicationPersistence FakePersistence { get; } = new();
    public FakeRegulatoryNumberSequenceCounterPersistence FakeCounters { get; } = new();
    public FakePendingUploadService FakePendingUploadService { get; } = new();
    public FakeCaseManagementAuthNonceStore FakeCaseManagementAuthNonceStore { get; } = new();

    public ICaseWorkingApiAdapter MockCaseWorkingAdapter { get; } =
        Substitute.For<ICaseWorkingApiAdapter>();
    public ICdpUploaderService MockCdpUploaderService { get; } =
        Substitute.For<ICdpUploaderService>();
    public IRecyclingOperationsAuditPersistence MockAuditPersistence { get; } =
        Substitute.For<IRecyclingOperationsAuditPersistence>();

    // The only fake for the ReEx seam — everything upstream of it (ReExClient,
    // HttpReExApiAdapter) is the real production code.
    public FakeReExHandler FakeReExHandler { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IAccreditationApplicationPersistence>(FakePersistence);
            services.AddSingleton<IRegulatoryNumberSequenceCounterPersistence>(FakeCounters);
            services.AddSingleton<IPendingUploadService>(FakePendingUploadService);
            services.AddSingleton<ICaseManagementAuthNonceStore>(FakeCaseManagementAuthNonceStore);
            services.AddSingleton(MockCaseWorkingAdapter);
            services.AddSingleton(MockCdpUploaderService);
            services.AddSingleton(MockAuditPersistence);

            // Force the real adapter regardless of which one Program.cs's own
            // Development/UseStub branch picked — same "last registration wins" mechanism
            // AccreditationApplicationTestFactory uses to install MockReExAdapter.
            services.AddSingleton<IReExApiAdapter, HttpReExApiAdapter>();

            // ReExClient's BaseUrl (bound from IOptions<ReExConfig>) is empty in this test
            // host, but that's fine: ConfigureHttpClient runs before ReExClient's own
            // constructor, and ReExClient only overwrites BaseAddress when its config value
            // is non-empty, so this assignment survives.
            services
                .AddHttpClient<IReExClient, ReExClient>()
                .ConfigureHttpClient(client => client.BaseAddress = new Uri("http://reex.test/"))
                .ConfigurePrimaryHttpMessageHandler(() => FakeReExHandler);
        });
    }
}

/// <summary>
/// Fakes only the ReEx HTTP transport. Routes by URL shape — organisations, ORS-R
/// (registration-scoped overseas-sites, no "accreditations" segment) and ORS-A
/// (accreditation-scoped overseas-sites) each get their own configurable body/status.
/// Mutable and reused across tests in a class, so each test sets what it needs and the
/// class's Reset() puts it back to empty-but-successful defaults.
/// </summary>
public class FakeReExHandler : HttpMessageHandler
{
    public string OrganisationJson { get; set; } = "{}";
    public HttpStatusCode OrganisationStatusCode { get; set; } = HttpStatusCode.OK;
    public string RegistrationSitesJson { get; set; } = "{}";
    public HttpStatusCode RegistrationSitesStatusCode { get; set; } = HttpStatusCode.OK;
    public string AccreditationSitesJson { get; set; } = "{}";
    public HttpStatusCode AccreditationSitesStatusCode { get; set; } = HttpStatusCode.OK;

    public List<string> RequestedPaths { get; } = [];

    public void Reset()
    {
        OrganisationJson = "{}";
        OrganisationStatusCode = HttpStatusCode.OK;
        RegistrationSitesJson = "{}";
        RegistrationSitesStatusCode = HttpStatusCode.OK;
        AccreditationSitesJson = "{}";
        AccreditationSitesStatusCode = HttpStatusCode.OK;
        RequestedPaths.Clear();
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var path = request.RequestUri!.AbsolutePath;
        RequestedPaths.Add(path);

        var isAccreditationSites =
            path.Contains("accreditations") && path.Contains("overseas-sites");
        var isRegistrationSites = !isAccreditationSites && path.Contains("overseas-sites");

        var (body, statusCode) =
            isAccreditationSites ? (AccreditationSitesJson, AccreditationSitesStatusCode)
            : isRegistrationSites ? (RegistrationSitesJson, RegistrationSitesStatusCode)
            : (OrganisationJson, OrganisationStatusCode);

        return Task.FromResult(
            new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }
        );
    }
}
