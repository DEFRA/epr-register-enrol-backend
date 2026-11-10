using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel;

/// <summary>The options every domain model transaction starts with.</summary>
internal static class DomainModelTransactions
{
    /// <summary>
    /// Reads from the primary. A deployed connection string can send reads to secondaries, and the driver refuses
    /// any read inside a transaction that does not read from the primary, so without this every transaction that
    /// reads failed in CDP. Reads outside a transaction keep whatever the connection string asks for.
    /// </summary>
    public static TransactionOptions Options { get; } = new(readPreference: ReadPreference.Primary);
}
