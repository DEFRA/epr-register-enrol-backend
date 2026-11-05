namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// What kind of application an <see cref="ApplicationEntity"/> is: one value per <see cref="ApplicationDetails"/>
/// class, and the same string as that class's stored discriminator (decision D1, RA-744). New and Reapply are both
/// <see cref="Accreditation"/>; <see cref="AccreditationDetails.Kind"/> tells them apart.
/// </summary>
public enum ApplicationType
{
    Accreditation,
    OrsChange,
    TonnageChange,
    NewRegistration,
    AnnualCharge,
}
