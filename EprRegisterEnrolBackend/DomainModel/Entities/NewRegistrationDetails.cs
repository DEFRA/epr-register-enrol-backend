using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// The details of an application for a new registration. The registration's own data sits on the Registration
/// created when the journey starts (the envelope's <see cref="ApplicationEntity.RegistrationId"/>); this holds only
/// where the operator has got to. The sections are named, not fixed fields, until the journey's sections are agreed.
/// </summary>
[BsonDiscriminator(nameof(ApplicationType.NewRegistration))]
public sealed class NewRegistrationDetails : ApplicationDetails
{
    public override ApplicationType ApplicationType => ApplicationType.NewRegistration;

    public List<RegistrationJourneySection> Sections { get; set; } = [];
}
