using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolBackend.Test.AccreditationApi;

// RA-651: against a real ephemeral Mongo, so the filters are the ones that run in production.
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

        var counts = await _persistence.CountUnservableApprovedAsync(2027, Ct);

        counts.Should().Be(new UnservableApprovedCounts(1, 2, 1));
        counts.Any.Should().BeTrue();
    }

    // The reviewer's point: the API's mapper drops a whitespace-only reference, so the count must too.
    [Fact]
    public async Task CountsAReferenceThatIsOnlyWhitespace_AsMissing()
    {
        await Add(registration: "   ", accreditation: "\t ");

        var counts = await _persistence.CountUnservableApprovedAsync(2027, Ct);

        counts.Should().Be(new UnservableApprovedCounts(0, 1, 1));
    }

    [Fact]
    public async Task IgnoresApplicationsThatAreNotApprovedOrAreBeforeTheFirstYear()
    {
        await Add(year: 2026, orgId: null, registration: null, accreditation: null);
        await Add(status: ApplicationStatus.Submitted, orgId: null, registration: null, accreditation: null);

        var counts = await _persistence.CountUnservableApprovedAsync(2027, Ct);

        counts.Should().Be(new UnservableApprovedCounts(0, 0, 0));
        counts.Any.Should().BeFalse();
    }

    [Fact]
    public async Task WithNothingStored_IsAllZero()
    {
        (await _persistence.CountUnservableApprovedAsync(2027, Ct)).Any.Should().BeFalse();
    }

    [Fact]
    public async Task AnAlreadyCancelledToken_StopsTheCount()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _persistence.CountUnservableApprovedAsync(2027, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
