namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

// RA-448: atomic per-pool sequence counters backing registration/accreditation
// number generation. IncrementAsync is the only mutating operation - callers
// never read-then-write CurrentMax themselves (see AC3 in the design doc).
public interface IRegulatoryNumberSequenceCounterPersistence
{
    // Atomically increments CurrentMax for the given composite key (e.g. "R-ER")
    // and returns the new value. Upserts the key at CurrentMax=1 if it doesn't
    // exist yet - callers relying on a pre-seeded floor (AC4) must ensure the
    // backfill step has run before this is ever called in an environment with
    // pre-existing numbers.
    Task<int> IncrementAsync(string key, CancellationToken ct = default);

    // Sets CurrentMax to the given value UNLESS the stored value is already
    // >= it - used by the backfill/seed step, never lowers an existing higher
    // value (AC4: idempotent, safe to re-run).
    Task SeedIfHigherAsync(string key, int value, CancellationToken ct = default);

    Task<int?> GetCurrentMaxAsync(string key, CancellationToken ct = default);
}
