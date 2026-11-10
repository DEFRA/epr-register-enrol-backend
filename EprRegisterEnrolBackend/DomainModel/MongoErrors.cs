using System.Diagnostics;
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

    /// <summary>
    /// The innermost method of this service the exception passed through, as <c>Type.Method</c>, or <c>unknown</c>.
    /// With <see cref="Kind"/> it places a failure without the message: method names hold no data. An async
    /// method runs as a compiler-generated state machine, so that frame is named after the method it came from.
    /// </summary>
    public static string Origin(Exception ex)
    {
        foreach (var frame in new StackTrace(ex, fNeedFileInfo: false).GetFrames())
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (type?.Namespace?.StartsWith(OwnNamespace, StringComparison.Ordinal) != true)
            {
                continue;
            }

            if (type.Name.StartsWith('<') && type.DeclaringType is { } owner)
            {
                var name = type.Name[1..type.Name.IndexOf('>')];
                // A lambda's state machine has no method name of its own; keep looking for a named one.
                if (name.Length > 0 && !name.Contains('<'))
                {
                    return $"{owner.Name}.{name}";
                }

                continue;
            }

            if (!method!.Name.Contains('<') && !type.Name.Contains('<'))
            {
                return $"{type.Name}.{method.Name}";
            }
        }

        return "unknown";
    }

    private const string OwnNamespace = "EprRegisterEnrolBackend";
}
