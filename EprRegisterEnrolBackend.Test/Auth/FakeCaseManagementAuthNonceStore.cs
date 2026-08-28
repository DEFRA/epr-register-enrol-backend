using System.Collections.Concurrent;
using EprRegisterEnrolBackend.Auth;

namespace EprRegisterEnrolBackend.Test.Auth;

// In-process stand-in for CaseManagementAuthNonceStore, so tests that don't need real Mongo
// behaviour (handler decision-logic tests, WebApplicationFactory hosts that otherwise avoid
// Mongo entirely - see AccreditationApplicationTestFactory) don't pay for one.
// ConcurrentDictionary.TryAdd is itself atomic per key, which is what lets
// ReplayedNonce_ConcurrentRequests_OnlyOneSucceeds correctly exercise single-use-under-
// contention without a real database. The real store's atomicity (a genuine unique-index
// insert) and its TTL/multi-instance behaviour are covered separately by
// CaseManagementAuthNonceStoreTests against a real ephemeral mongod.
public class FakeCaseManagementAuthNonceStore : ICaseManagementAuthNonceStore
{
    private readonly ConcurrentDictionary<string, byte> _consumed = new();

    public Task<bool> TryConsumeAsync(string nonce, TimeSpan ttl, CancellationToken ct = default) =>
        Task.FromResult(_consumed.TryAdd(nonce, 0));
}
