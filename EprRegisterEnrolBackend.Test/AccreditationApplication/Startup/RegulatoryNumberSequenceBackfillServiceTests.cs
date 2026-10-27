using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

public class RegulatoryNumberSequenceBackfillServiceTests
{
    // RA-690: accreditation references are derived from the registration reference, never
    // drawn from a counter - so only the eight registration pools are seeded, with the
    // starting values unchanged from RA-448.
    private static readonly IReadOnlyDictionary<string, int> RegistrationSeedValues =
        new Dictionary<string, int>
        {
            ["R-ER"] = 337,
            ["R-EX"] = 341,
            ["R-NR"] = 91,
            ["R-NX"] = 92,
            ["R-SR"] = 71,
            ["R-SX"] = 69,
            ["R-WR"] = 74,
            ["R-WX"] = 73,
        };

    private static (
        RegulatoryNumberSequenceBackfillService Service,
        FakeRegulatoryNumberSequenceCounterPersistence Store,
        RegulatoryNumberBackfillStatus Status
    ) Create()
    {
        var store = new FakeRegulatoryNumberSequenceCounterPersistence();
        var status = new RegulatoryNumberBackfillStatus();
        var service = new RegulatoryNumberSequenceBackfillService(
            store,
            status,
            EnabledNullLogger<RegulatoryNumberSequenceBackfillService>.Instance
        );
        return (service, store, status);
    }

    [Fact]
    public async Task SeedAllAsync_seeds_exactly_the_eight_registration_pools_with_their_starting_values()
    {
        var (service, store, _) = Create();

        await service.SeedAllAsync(CancellationToken.None);

        store.Keys.Should().BeEquivalentTo(RegistrationSeedValues.Keys);
        foreach (var (key, value) in RegistrationSeedValues)
        {
            (await store.GetCurrentMaxAsync(key, TestContext.Current.CancellationToken))
                .Should()
                .Be(value, $"{key} should be seeded to its starting value");
        }
    }

    [Fact]
    public async Task SeedAllAsync_leaves_an_existing_accreditation_counter_untouched()
    {
        // RA-690: retired A-* counter documents stay in Mongo as they are - no migration,
        // no deletion, and the backfill no longer raises them either.
        var (service, store, _) = Create();
        store.Seed("A-ER", 5);

        await service.SeedAllAsync(CancellationToken.None);

        (await store.GetCurrentMaxAsync("A-ER", TestContext.Current.CancellationToken))
            .Should()
            .Be(5);
    }

    [Fact]
    public async Task SeedAllAsync_run_twice_never_lowers_an_already_seeded_value()
    {
        var (service, store, _) = Create();
        // Simulates a real number already having been issued between the snapshot
        // and this seed step running - the backfill must never regress it.
        store.Seed("R-ER", 9999);

        await service.SeedAllAsync(CancellationToken.None);
        await service.SeedAllAsync(CancellationToken.None);

        (await store.GetCurrentMaxAsync("R-ER", TestContext.Current.CancellationToken))
            .Should()
            .Be(9999);
    }

    [Fact]
    public async Task SeedAllAsync_raises_a_key_below_the_seed_value()
    {
        var (service, store, _) = Create();
        store.Seed("R-ER", 1);

        await service.SeedAllAsync(CancellationToken.None);

        (await store.GetCurrentMaxAsync("R-ER", TestContext.Current.CancellationToken))
            .Should()
            .Be(337);
    }

    [Fact]
    public async Task SeedAllAsync_marks_the_shared_status_complete()
    {
        var (service, _, status) = Create();
        status.IsComplete.Should().BeFalse("nothing has been seeded yet");

        await service.SeedAllAsync(CancellationToken.None);

        status.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Readiness_turns_healthy_once_the_registration_only_seed_completes()
    {
        var (service, _, status) = Create();
        var check = new RegulatoryNumberBackfillHealthCheck(status);
        var ct = TestContext.Current.CancellationToken;

        (await check.CheckHealthAsync(new HealthCheckContext(), ct))
            .Status.Should()
            .Be(HealthStatus.Unhealthy);

        await service.SeedAllAsync(ct);

        (await check.CheckHealthAsync(new HealthCheckContext(), ct))
            .Status.Should()
            .Be(HealthStatus.Healthy);
    }
}
