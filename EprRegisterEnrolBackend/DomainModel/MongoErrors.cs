using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel;

/// <summary>How the dual-writes classify a failed write, without quoting the driver's message.</summary>
internal static class MongoErrors
{
    /// <summary>A unique index rejected the write: a concurrent writer committed the same key first.</summary>
    public static bool IsDuplicateKey(MongoException ex) =>
        ex switch
        {
            MongoWriteException write => write.WriteError?.Category
                == ServerErrorCategory.DuplicateKey,
            MongoCommandException command => command.Code == 11000,
            _ => false,
        };

    /// <summary>
    /// The exception's type and Mongo error code, which is safe to log. The message is not: a driver message
    /// can quote document values, and logs must hold no personal data (design doc §6.1).
    /// </summary>
    public static string Kind(Exception ex) =>
        ex switch
        {
            MongoWriteException write => $"{ex.GetType().Name}:{write.WriteError?.Code}",
            MongoCommandException command => $"{ex.GetType().Name}:{command.Code}",
            _ => ex.GetType().Name,
        };
}
