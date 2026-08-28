using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Test.Auth;
using EprRegisterEnrolBackend.Test.CdpUploader;
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

    // epr-register-enrol-backend-6y2: real PendingUploadService is now Mongo-backed, so
    // WebApplicationFactory tests that never actually exercise Mongo behaviour use this
    // in-memory stand-in instead - matches how FakePersistence/FakeCounters above avoid
    // needing a real Mongo instance too.
    public FakePendingUploadService FakePendingUploadService { get; } = new();

    // epr-register-enrol-backend-0i1: same reasoning as FakePendingUploadService above - the
    // real CaseManagementAuthNonceStore is Mongo-backed, and this factory otherwise keeps its
    // whole test host Mongo-free.
    public FakeCaseManagementAuthNonceStore FakeCaseManagementAuthNonceStore { get; } = new();

    public IReExApiAdapter MockReExAdapter { get; } = Substitute.For<IReExApiAdapter>();
    public ICaseWorkingApiAdapter MockCaseWorkingAdapter { get; } =
        Substitute.For<ICaseWorkingApiAdapter>();
    public ICdpUploaderService MockCdpUploaderService { get; } =
        Substitute.For<ICdpUploaderService>();

    // RA-469 AC15/AC19: NSubstitute mock, matching the other adapter mocks above, rather than a
    // real MongoService<T>-backed persistence - RecyclingOperationsEndpointTests only needs to
    // assert RecordAsync was (or wasn't) called with the right fields, never to query stored
    // records back.
    public IRecyclingOperationsAuditPersistence MockAuditPersistence { get; } =
        Substitute.For<IRecyclingOperationsAuditPersistence>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IAccreditationApplicationPersistence>(FakePersistence);
            services.AddSingleton<IRegulatoryNumberSequenceCounterPersistence>(FakeCounters);
            services.AddSingleton<IPendingUploadService>(FakePendingUploadService);
            services.AddSingleton<ICaseManagementAuthNonceStore>(FakeCaseManagementAuthNonceStore);
            services.AddSingleton(MockReExAdapter);
            services.AddSingleton(MockCaseWorkingAdapter);
            services.AddSingleton(MockCdpUploaderService);
            services.AddSingleton(MockAuditPersistence);
        });
    }
}
