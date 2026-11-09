using EprRegisterEnrolBackend.AccreditationApi;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApi;

// RA-651: the check that tells operators about approved applications the API cannot serve. The
// log line is what the existing "5xx and Errors" alert reads, so its level is part of the contract.
public class UnservableAccreditationCheckServiceTests
{
    private readonly IAccreditationApplicationPersistence _persistence =
        Substitute.For<IAccreditationApplicationPersistence>();
    private readonly CapturingLoggerFactory _logs = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UnservableAccreditationCheckService Service(
        AccreditationApiMonitoringConfig? monitoring = null,
        int firstYear = 2027
    ) =>
        new(
            _persistence,
            Options.Create(new ServiceClientsConfig { FirstAccreditationYear = firstYear }),
            Options.Create(monitoring ?? new AccreditationApiMonitoringConfig()),
            _logs.CreateLogger<UnservableAccreditationCheckService>()
        );

    [Fact]
    public async Task RunOnce_WhenEveryRecordCanBeServed_LogsInformation_NotAnError()
    {
        _persistence.CountUnservableApprovedAsync(2027).Returns(new UnservableApprovedCounts(0, 0, 0));

        await Service().RunOnceAsync();

        var entry = _logs.Entries.Should().ContainSingle().Subject;
        entry.LogLevel.Should().Be(LogLevel.Information);
        entry.Properties["FromYear"].Should().Be(2027);
    }

    [Fact]
    public async Task RunOnce_WhenSomeCannotBeServed_LogsOneErrorWithTheCounts()
    {
        _persistence.CountUnservableApprovedAsync(2028).Returns(new UnservableApprovedCounts(1, 2, 3));

        await Service(firstYear: 2028).RunOnceAsync();

        var entry = _logs.Entries.Should().ContainSingle().Subject;
        entry.LogLevel.Should().Be(LogLevel.Error);
        entry.Message.Should().Be(
            "Approved accreditations the accreditation API cannot serve: noOrganisationNumber=1 noRegistrationNumber=2 noAccreditationNumber=3 fromYear=2028"
        );
        // The fields a dashboard or alert filters on.
        entry.Properties["NoOrganisationNumber"].Should().Be(1);
        entry.Properties["NoRegistrationNumber"].Should().Be(2);
        entry.Properties["NoAccreditationNumber"].Should().Be(3);
        entry.Properties["FromYear"].Should().Be(2028);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public async Task RunOnce_AnySingleReason_IsAnError(int organisation, int registration, int accreditation)
    {
        _persistence
            .CountUnservableApprovedAsync(2027)
            .Returns(new UnservableApprovedCounts(organisation, registration, accreditation));

        await Service().RunOnceAsync();

        _logs.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error);
    }

    [Fact]
    public async Task RunOnce_WhenTheCountFails_LogsAWarning_AndDoesNotThrow()
    {
        _persistence
            .CountUnservableApprovedAsync(2027)
            .ThrowsAsync(new InvalidOperationException("mongo is down"));

        var act = () => Service().RunOnceAsync();

        await act.Should().NotThrowAsync();
        _logs.Entries.Should().ContainSingle().Which.LogLevel.Should().Be(LogLevel.Warning);
        _logs.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
    }

    [Fact]
    public async Task WhenSwitchedOff_NeverTouchesTheDatabase()
    {
        var service = Service(new AccreditationApiMonitoringConfig { Enabled = false });

        await service.StartAsync(Ct);
        await service.StopAsync(Ct);

        await _persistence.DidNotReceiveWithAnyArgs().CountUnservableApprovedAsync(default);
    }

    [Fact]
    public async Task Once_Started_ChecksAgainOnEveryInterval_AndStopsCleanly()
    {
        _persistence.CountUnservableApprovedAsync(2027).Returns(new UnservableApprovedCounts(0, 0, 0));
        var service = Service(
            new AccreditationApiMonitoringConfig
            {
                InitialDelay = TimeSpan.Zero,
                Interval = TimeSpan.FromMilliseconds(20),
            }
        );

        await service.StartAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_logs.Entries.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10, Ct);
        var stopped = await Record.ExceptionAsync(() => service.StopAsync(Ct));

        stopped.Should().BeNull();
        _logs.Entries.Count.Should().BeGreaterThanOrEqualTo(2);
    }
}

// Against a real ephemeral Mongo, so the filters are the ones that run in production.
public class UnservableApprovedCountTests : IDisposable
{
    private readonly EphemeralMongoTestFactory _factory;
    private readonly IAccreditationApplicationPersistence _persistence;

    public UnservableApprovedCountTests(MongoIntegrationFixture fixture)
    {
        _factory = new EphemeralMongoTestFactory(
            fixture,
            "unservable_count",
            Environments.Production,
            new Dictionary<string, string?> { ["CaseWorking:UseStub"] = "true" }
        );
        _ = _factory.CreateClient();
        _persistence = _factory.Services.GetRequiredService<IAccreditationApplicationPersistence>();
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task Add(
        int year = 2027,
        ApplicationStatus status = ApplicationStatus.Approved,
        int? orgId = 500027,
        string? registration = "R27EX5000270012AL",
        string? accreditation = "A27EX5000270012AL"
    )
    {
        var created = await _persistence.CreateAsync(
            new AccreditationApplicationModel
            {
                OrganisationId = "reex-object-id",
                OrgId = orgId,
                Year = year,
                MaterialType = MaterialType.Aluminium,
                ApplicationStatus = status,
                RegistrationReference = registration,
                AccreditationReference = accreditation,
            }
        );
        created.Should().NotBeNull();
    }

    [Fact]
    public async Task Counts_EachReason_ForApprovedApplicationsFromTheFirstYear()
    {
        await Add(); // fine
        await Add(orgId: null); // no organisation number
        await Add(registration: ""); // no registration number
        await Add(registration: null, accreditation: null); // neither registration nor accreditation number

        var counts = await _persistence.CountUnservableApprovedAsync(2027);

        counts.Should().Be(new UnservableApprovedCounts(1, 2, 1));
        counts.Any.Should().BeTrue();
    }

    [Fact]
    public async Task IgnoresApplicationsThatAreNotApprovedOrAreBeforeTheFirstYear()
    {
        await Add(year: 2026, orgId: null, registration: null, accreditation: null);
        await Add(status: ApplicationStatus.Submitted, orgId: null, registration: null, accreditation: null);

        var counts = await _persistence.CountUnservableApprovedAsync(2027);

        counts.Should().Be(new UnservableApprovedCounts(0, 0, 0));
        counts.Any.Should().BeFalse();
    }

    [Fact]
    public async Task WithNothingStored_IsAllZero()
    {
        (await _persistence.CountUnservableApprovedAsync(2027)).Any.Should().BeFalse();
    }
}
