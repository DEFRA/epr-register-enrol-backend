using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>What happened when a collection's <c>$jsonSchema</c> validator was applied (RA-758).</summary>
public enum ValidatorApplyResult
{
    /// <summary>The collection has no validator, so there was nothing to apply.</summary>
    NoValidator,

    /// <summary>collMod (or create) succeeded.</summary>
    Applied,

    /// <summary>It failed in a way that may clear within seconds, such as a dropped connection or a failover.</summary>
    Transient,

    /// <summary>The database user may not run collMod. That will not change on a retry.</summary>
    NotPermitted,
}

/// <summary>Sorts a failure to apply a validator into one worth retrying and one that is not (RA-758).</summary>
public static class ValidatorApplyFailure
{
    private const int Unauthorized = 13;

    // Atlas reports an action the user's role does not allow as AtlasError rather than Unauthorized, but it uses the
    // same code for many other errors, so only its permission message counts.
    private const int AtlasError = 8000;
    private const string AtlasNotAllowed = "not allowed to do action";

    public static ValidatorApplyResult Classify(Exception exception) =>
        exception switch
        {
            MongoCommandException { Code: Unauthorized } => ValidatorApplyResult.NotPermitted,
            MongoCommandException { Code: AtlasError } atlas
                when atlas.ErrorMessage?.Contains(
                    AtlasNotAllowed,
                    StringComparison.OrdinalIgnoreCase
                ) == true => ValidatorApplyResult.NotPermitted,
            _ => ValidatorApplyResult.Transient,
        };
}
