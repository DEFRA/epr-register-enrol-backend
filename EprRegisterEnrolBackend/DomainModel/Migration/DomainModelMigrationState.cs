using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>
/// The one document in <c>domainModelMigrations</c>: who holds the lease, the latest pass's counts, and the groups it
/// could not migrate. What is left to migrate is not recorded here, it is whatever has no current version in
/// <c>applicationVersions</c>, so a restart can never lose its place.
/// </summary>
public sealed class DomainModelMigrationState
{
    public const string ApplicationsMigration = "applications";

    [BsonId]
    public string Id { get; set; } = ApplicationsMigration;

    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public DateTime? PassStartedAt { get; set; }
    public DateTime? PassCompletedAt { get; set; }
    public long Examined { get; set; }
    public long AlreadyMigrated { get; set; }
    public long Migrated { get; set; }
    public List<DomainModelMigrationFailure> Failures { get; set; } = [];
}

/// <summary>A registration group that could not be migrated. Ids and the error kind only, never personal data.</summary>
public sealed class DomainModelMigrationFailure
{
    /// <summary>The organisation and registration, or the application's own id when it has no registration yet.</summary>
    public required string Group { get; set; }

    /// <summary>The application the attempt started from: enough to retry it.</summary>
    public ObjectId ApplicationId { get; set; }
    public int Attempts { get; set; }
    public required string LastErrorKind { get; set; }
    public DateTime LastFailedAt { get; set; }
}
