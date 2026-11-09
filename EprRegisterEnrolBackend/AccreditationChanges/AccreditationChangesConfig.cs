namespace EprRegisterEnrolBackend.AccreditationChanges;

public class AccreditationChangesConfig
{
    public const string SectionName = "AccreditationChanges";

    /// <summary>
    /// RA-492: when true, submitting a change request also creates an add-overseas-site work
    /// item in Case Management. Off by default: ManagementBe on main only accepts the
    /// re-accreditation type, so this must stay off until ManagementBe registers
    /// add-overseas-site (deploy ManagementBe first, then switch this on).
    /// </summary>
    public bool SubmitToCaseManagement { get; set; }
}
