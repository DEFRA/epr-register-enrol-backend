using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Reconciliation;

/// <summary>
/// What differs between a legacy document and its projection. <see cref="ModelOnly"/> is the part of
/// <see cref="Paths"/> where legacy has nothing and the model has a value, which needs explaining: where did it come from?
/// </summary>
internal sealed record DocumentDifferences(HashSet<string> Paths, HashSet<string> ModelOnly);

/// <summary>
/// Which fields of two documents differ, as paths such as <c>overseasSites.sites[].siteName</c>. Paths hold element
/// names only, never a value, so they are safe to log. Blank on both sides is no difference, whether blank is missing,
/// null, an empty string or an empty list. Lists of different lengths are always different.
/// </summary>
internal static class BsonDifferences
{
    public static DocumentDifferences Compare(BsonDocument legacy, BsonDocument projected)
    {
        var differences = new DocumentDifferences(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal)
        );
        Walk(legacy, projected, string.Empty, differences);
        return differences;
    }

    private static void Walk(
        BsonValue legacy,
        BsonValue projected,
        string path,
        DocumentDifferences differences
    )
    {
        if (IsBlank(legacy) && IsBlank(projected))
        {
            return;
        }

        switch (legacy, projected)
        {
            case (BsonDocument a, BsonDocument b):
                WalkDocuments(a, b, path, differences);
                break;
            case (BsonArray a, BsonArray b) when a.Count == b.Count:
                for (var i = 0; i < a.Count; i++)
                {
                    Walk(a[i], b[i], $"{path}[]", differences);
                }

                break;
            default:
                if (!legacy.Equals(projected))
                {
                    differences.Paths.Add(path);
                    if (IsBlank(legacy))
                    {
                        differences.ModelOnly.Add(path);
                    }
                }

                break;
        }
    }

    private static void WalkDocuments(
        BsonDocument legacy,
        BsonDocument projected,
        string path,
        DocumentDifferences differences
    )
    {
        foreach (var name in legacy.Names.Union(projected.Names))
        {
            Walk(
                legacy.GetValue(name, BsonNull.Value),
                projected.GetValue(name, BsonNull.Value),
                path.Length == 0 ? name : $"{path}.{name}",
                differences
            );
        }
    }

    private static bool IsBlank(BsonValue value) =>
        value.IsBsonNull
        || value is BsonString { Value.Length: 0 }
        || value is BsonArray { Count: 0 };
}
