using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.TestSupport;

public static class MongoConnectionStrings
{
    /// <summary>
    /// <paramref name="connectionString"/> with reads sent to secondaries where there are any, as a CDP connection
    /// string may ask for. The driver refuses a read inside a transaction unless the transaction reads from the
    /// primary, so this is what shows a transaction has pinned it.
    /// </summary>
    public static string ReadingFromSecondaries(string connectionString) =>
        new MongoUrlBuilder(connectionString)
        {
            ReadPreference = ReadPreference.SecondaryPreferred,
        }.ToString();
}
