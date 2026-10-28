namespace EprRegisterEnrolBackend.DomainModel.Versioning;

/// <summary>
/// Marks a member that describes a version or an import rather than the entity itself. The repository ignores it
/// when deciding whether an update changed anything, so a refresh that only moves such a value writes no version
/// (design doc §4.2). It applies to members of the entity and of class-mapped objects nested in it, not to the
/// elements of a list.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ExcludedFromChangeDetectionAttribute : Attribute;
