using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

public class RegulatoryNumberSequenceBackfillServiceTests
{
    private static readonly string[] AllSixteenKeys =
    [
        "R-ER",
        "R-EX",
        "R-NR",
        "R-NX",
        "R-SR",
        "R-SX",
        "R-WR",
        "R-WX",
        "A-ER",
        "A-EX",
        "A-NR",
        "A-NX",
        "A-SR",
        "A-SX",
        "A-WR",
        "A-WX",
    ];

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
            NullLogger<RegulatoryNumberSequenceBackfillService>.Instance
        );
        return (service, store, status);
    }

    [Fact]
    public async Task SeedAllAsync_seeds_all_16_pool_keys()
    {
        var (service, store, _) = Create();

        await service.SeedAllAsync(CancellationToken.None);

        foreach (var key in AllSixteenKeys)
        {
            (await store.GetCurrentMaxAsync(key))
                .Should()
                .NotBeNull($"{key} should have been seeded");
        }
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

        (await store.GetCurrentMaxAsync("R-ER")).Should().Be(9999);
    }

    [Fact]
    public async Task SeedAllAsync_raises_a_key_below_the_seed_value()
    {
        var (service, store, _) = Create();
        store.Seed("R-ER", 1);

        await service.SeedAllAsync(CancellationToken.None);

        (await store.GetCurrentMaxAsync("R-ER")).Should().Be(337);
    }

    [Fact]
    public async Task SeedAllAsync_marks_the_shared_status_complete()
    {
        var (service, _, status) = Create();
        status.IsComplete.Should().BeFalse("nothing has been seeded yet");

        await service.SeedAllAsync(CancellationToken.None);

        status.IsComplete.Should().BeTrue();
    }
}
