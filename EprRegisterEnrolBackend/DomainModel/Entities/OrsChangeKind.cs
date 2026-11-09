namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// What an in-year ORS change does to its ORS. One application can carry several, because only one change can be
/// open per ORS at a time: an edit and interim site changes to the same ORS travel together.
/// </summary>
public enum OrsChangeKind
{
    /// <summary>Adds an ORS already on the registration to the Accreditation.</summary>
    AddRegisteredSite,

    /// <summary>Adds a new ORS, proposed by this application, to the Accreditation.</summary>
    AddNewSite,

    /// <summary>Edits an accredited ORS.</summary>
    EditSite,

    /// <summary>Adds, edits or removes the ORS's interim sites.</summary>
    ChangeInterimSites,
}
