using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>A domain model collection whose existence, indexes and validator the schema check can verify.</summary>
public interface IDomainModelCollection
{
    string CollectionName { get; }

    /// <summary>Names of the indexes this collection is meant to have (excluding the implicit <c>_id_</c>).</summary>
    IReadOnlyList<string> GetExpectedIndexNames();

    /// <summary>Names of the indexes the server has now. Empty when the collection does not exist yet.</summary>
    Task<IReadOnlyList<string>> GetIndexNamesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The <c>validator</c>, <c>validationLevel</c> and <c>validationAction</c> this collection is meant to have, or
    /// null when it has no validator.
    /// </summary>
    BsonDocument? GetExpectedValidation() => null;

    /// <summary>The same three settings as the server has them now, or null when it has no validator.</summary>
    Task<BsonDocument?> GetValidationAsync(CancellationToken cancellationToken) =>
        Task.FromResult<BsonDocument?>(null);
}
