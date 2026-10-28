namespace EprRegisterEnrolBackend.DomainModel.Seeding;

/// <summary>The seed dual-writer while <c>DomainModel:DualWriteSeed</c> is off: Seed behaves exactly as before.</summary>
public sealed class NoOpSeedDualWriter : ISeedDualWriter
{
    public Task WriteAsync(SeedDualWrite seed, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
