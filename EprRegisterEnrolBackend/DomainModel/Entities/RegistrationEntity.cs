using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// One version of a Registration, in <c>registrationVersions</c>. Registrations follow ReEx's lifecycle until
/// this service implements new registrations. The further ReEx fields (permits, yearly metrics, approved
/// persons and so on) arrive with enrichment, which is deferred (Q35).
/// </summary>
public class RegistrationEntity : VersionedEntityBase
{
    /// <summary>The Organisation's <c>_id</c> (Organisation is not versioned).</summary>
    public ObjectId OrganisationId { get; set; }

    public string? ReExRegistrationId { get; set; }

    /// <summary>Minted here, not in ReEx. Unique among current versions.</summary>
    public string? RegistrationNumber { get; set; }

    public List<string> PreviousRegistrationNumbers { get; set; } = [];

    /// <summary>
    /// RA-763: the accreditation numbers ReEx issued for this registration, one per year, oldest first, written at
    /// seed. A year with no entry was registration only, or has not been seeded.
    /// </summary>
    public List<AccreditationNumberForYear> AccreditationNumbers { get; set; } = [];

    [BsonRepresentation(BsonType.String)]
    public RegistrationStatus Status { get; set; } = RegistrationStatus.Created;

    [BsonRepresentation(BsonType.String)]
    public WasteProcessingKind WasteProcessingType { get; set; }

    [BsonRepresentation(BsonType.String)]
    public MaterialType Material { get; set; }

    [BsonRepresentation(BsonType.String)]
    public GlassRecyclingProcess? GlassRecyclingProcess { get; set; }

    [BsonRepresentation(BsonType.String)]
    public Nation? Nation { get; set; }

    public RegistrationSite? Site { get; set; }
    public List<string> PermitNumbers { get; set; } = [];
    public PostalAddress? NoticeAddress { get; set; }
    public ContactPerson? ContactDetails { get; set; }
    public ContactPerson? SubmitterContactDetails { get; set; }
    public DateTime? FormSubmissionTime { get; set; }
    public EntitySource Source { get; set; } = new();

    /// <summary>
    /// RA-763: holds <paramref name="accreditationNumber"/> for <paramref name="year"/> in
    /// <see cref="AccreditationNumbers"/>, keeping one entry per year, oldest first. When the year already has a
    /// different number, <paramref name="whenHeld"/> says which one stays: a seed replaces it, because ReEx masters the
    /// number, and the migrator keeps it, because the seed wrote it from ReEx more recently than any application did.
    /// </summary>
    /// <returns>True when the year already held a different number, so the caller can record the disagreement.</returns>
    public bool SetAccreditationNumber(
        int year,
        string accreditationNumber,
        HeldAccreditationNumber whenHeld
    )
    {
        var held = AccreditationNumbers.Find(a => a.Year == year);
        if (held is null)
        {
            AccreditationNumbers.Add(
                new AccreditationNumberForYear
                {
                    Year = year,
                    AccreditationNumber = accreditationNumber,
                }
            );
            AccreditationNumbers.Sort((a, b) => a.Year.CompareTo(b.Year));
            return false;
        }

        if (held.AccreditationNumber == accreditationNumber)
        {
            return false;
        }

        if (whenHeld == HeldAccreditationNumber.Replace)
        {
            held.AccreditationNumber = accreditationNumber;
        }

        return true;
    }
}

/// <summary>What <see cref="RegistrationEntity.SetAccreditationNumber"/> does with a different number already held for the year.</summary>
public enum HeldAccreditationNumber
{
    Replace,
    Keep,
}