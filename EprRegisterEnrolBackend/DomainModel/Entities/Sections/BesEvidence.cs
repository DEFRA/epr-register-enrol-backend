namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

/// <summary>BES evidence uploaded for one overseas site (legacy <c>BesEvidenceModel</c>).</summary>
public class BesEvidence
{
    public List<BesEvidenceFile> BesEvidenceUploads { get; set; } = [];
    public bool DoYouWantToUploadMoreEvidence { get; set; } = true;
}
