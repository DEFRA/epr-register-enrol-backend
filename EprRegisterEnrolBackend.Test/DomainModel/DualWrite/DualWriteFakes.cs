using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Migration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.DualWrite;

// Hand-written stand-ins for the dual-write's own parts, used where a test must force a model failure or watch the
// order of the unit of work's steps. Kept small and explicit rather than mocked (README: no mocking).

/// <summary>A model sync that always fails, to prove a failed model write rolls the legacy write back.</summary>
internal sealed class FailingModelSync : IApplicationModelSync
{
    public Task SyncAsync(
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    ) => throw new InvalidOperationException("model write failed");
}

/// <summary>A model sync that records each call and runs <see cref="OnSync"/>, when set, in its place.</summary>
internal sealed class RecordingModelSync : IApplicationModelSync
{
    public List<(ObjectId ApplicationId, IClientSessionHandle Session)> Calls { get; } = [];

    public Func<ObjectId, IClientSessionHandle, Task>? OnSync { get; set; }

    public Task SyncAsync(
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        Calls.Add((applicationId, session));
        return OnSync?.Invoke(applicationId, session) ?? Task.CompletedTask;
    }
}

/// <summary>
/// A group migrator that records each call and answers with <see cref="OnMigrate"/>, when set, or with nothing to
/// do.
/// </summary>
internal sealed class RecordingGroupMigrator : IApplicationGroupMigrator
{
    public List<(ObjectId ApplicationId, IClientSessionHandle Session)> Calls { get; } = [];

    public Func<ObjectId, IClientSessionHandle, GroupMigrationResult>? OnMigrate { get; set; }

    public Task<GroupMigrationResult> EnsureMigratedAsync(
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        Calls.Add((applicationId, session));
        return Task.FromResult(
            OnMigrate?.Invoke(applicationId, session) ?? GroupMigrationResult.NothingToDo
        );
    }
}
