namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>A domain model collection whose existence and indexes the schema check can verify.</summary>
public interface IDomainModelCollection
{
    string CollectionName { get; }

    /// <summary>Names of the indexes this collection is meant to have (excluding the implicit <c>_id_</c>).</summary>
    IReadOnlyList<string> GetExpectedIndexNames();

    /// <summary>Names of the indexes the server has now. Empty when the collection does not exist yet.</summary>
    Task<IReadOnlyList<string>> GetIndexNamesAsync(CancellationToken cancellationToken);
}
