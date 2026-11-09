namespace EprRegisterEnrolBackend.DomainModel.Versioning;

/// <param name="Entity">The current version after the write (or the unchanged current version).</param>
/// <param name="Changed">False when the update changed nothing and so wrote no version.</param>
public record VersionedWriteResult<T>(T Entity, bool Changed);
