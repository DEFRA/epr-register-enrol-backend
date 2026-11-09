using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Reconciliation;

/// <summary>
/// Which fields of two documents differ, as paths such as <c>overseasSites.sites[].siteName</c>. Paths hold element
/// names only, never a value, so they are safe to log. Missing, null, an empty string and an empty list all count as
/// the same thing: the stored documents omit empty values inconsistently, and none of them means anything different.
/// </summary>
internal static class BsonDifferences
{
    public static HashSet<string> Paths(BsonDocument left, BsonDocument right)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        Walk(left, right, string.Empty, paths);
        return paths;
    }

    private static void Walk(BsonValue left, BsonValue right, string path, HashSet<string> paths)
    {
        if (IsEmpty(left) && IsEmpty(right))
        {
            return;
        }

        switch (left, right)
        {
            case (BsonDocument a, BsonDocument b):
                WalkDocuments(a, b, path, paths);
                break;
            case (BsonArray a, BsonArray b) when a.Count == b.Count:
                for (var i = 0; i < a.Count; i++)
                {
                    Walk(a[i], b[i], $"{path}[]", paths);
                }

                break;
            default:
                if (!left.Equals(right))
                {
                    paths.Add(path);
                }

                break;
        }
    }

    private static void WalkDocuments(
        BsonDocument left,
        BsonDocument right,
        string path,
        HashSet<string> paths
    )
    {
        foreach (var name in left.Names.Union(right.Names))
        {
            Walk(
                left.GetValue(name, BsonNull.Value),
                right.GetValue(name, BsonNull.Value),
                path.Length == 0 ? name : $"{path}.{name}",
                paths
            );
        }
    }

    private static bool IsEmpty(BsonValue value) =>
        value.IsBsonNull
        || value is BsonString { Value.Length: 0 }
        || value is BsonArray { Count: 0 };
}
