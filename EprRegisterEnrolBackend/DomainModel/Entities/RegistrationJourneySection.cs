using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>One section of the new registration journey and how far the operator has got with it.</summary>
public class RegistrationJourneySection
{
    public required string Name { get; set; }

    [BsonRepresentation(BsonType.String)]
    public SectionStatus Status { get; set; } = SectionStatus.NotStarted;
}
