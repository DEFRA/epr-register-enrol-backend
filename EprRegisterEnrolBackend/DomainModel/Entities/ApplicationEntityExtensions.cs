namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>Typed access to an <see cref="ApplicationEntity"/>'s <see cref="ApplicationDetails"/>.</summary>
public static class ApplicationEntityExtensions
{
    /// <summary>
    /// The details of an application known to be a New or Reapply accreditation application, such as one
    /// projected from the legacy collection.
    /// </summary>
    /// <exception cref="InvalidOperationException">The application is another type.</exception>
    public static AccreditationDetails AsAccreditation(this ApplicationEntity application) =>
        application.Details as AccreditationDetails
        ?? throw new InvalidOperationException(
            $"Application {application.EntityId} is a {application.ApplicationType} application, not an accreditation application."
        );
}
