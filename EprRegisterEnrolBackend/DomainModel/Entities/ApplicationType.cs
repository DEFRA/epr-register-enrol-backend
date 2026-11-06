namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// What kind of application an <see cref="ApplicationEntity"/> is: one value per <see cref="ApplicationDetails"/>
/// class, and the same string as that class's stored discriminator ("Envelope rules" in
/// register-enrol-application-types.md). New and Reapply are both <see cref="Accreditation"/>;
/// <see cref="AccreditationDetails.Kind"/> tells them apart. Only <see cref="Accreditation"/> has a details class so
/// far; the other four values are reserved for the types that arrive with their own details classes.
/// </summary>
public enum ApplicationType
{
    Accreditation,
    OrsChange,
    TonnageChange,
    NewRegistration,
    AnnualCharge,
}
