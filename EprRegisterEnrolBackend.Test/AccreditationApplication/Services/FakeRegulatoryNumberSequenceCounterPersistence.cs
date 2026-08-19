using System.Collections.Concurrent;
using EprRegisterEnrolBackend.AccreditationApplication.Services;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// RA-448: in-memory stand-in for the real Mongo-backed atomic counter, so
// generator/endpoint tests don't need a real Mongo instance. ConcurrentDictionary's
// AddOrUpdate is itself atomic per key, which is what lets this fake correctly
// exercise the "N concurrent callers, no duplicate sequence values" requirement
// (AC3) without a real database.
public class FakeRegulatoryNumberSequenceCounterPersistence
    : IRegulatoryNumberSequenceCounterPersistence
{
    private readonly ConcurrentDictionary<string, int> _store = new();

    public void Seed(string key, int currentMax) => _store[key] = currentMax;

    public void Clear() => _store.Clear();

    public Task<int> IncrementAsync(string key, CancellationToken ct = default)
    {
        var next = _store.AddOrUpdate(key, 1, (_, current) => current + 1);
        return Task.FromResult(next);
    }

    public Task SeedIfHigherAsync(string key, int value, CancellationToken ct = default)
    {
        _store.AddOrUpdate(key, value, (_, current) => Math.Max(current, value));
        return Task.CompletedTask;
    }

    public Task<int?> GetCurrentMaxAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_store.TryGetValue(key, out var value) ? value : (int?)null);
}
