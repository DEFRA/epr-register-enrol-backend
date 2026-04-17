using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace EprRegisterEnrolBackend.Organisation.Models;

public class OrganisationModel
{
    [BsonId(IdGenerator = typeof(ObjectIdGenerator))]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public ObjectId? Id { get; init; }

    public required string CompanyName { get; set; }

    public required string CompaniesHouseNumber { get; set; }

    public required string SchemeRegistrationId { get; set; }

    public required string RegisteredAddress { get; set; }

    public required string ApprovedPerson { get; set; }

    public List<DirectorModel>? Directors { get; set; }

    public DateTime? Created { get; set; } = DateTime.UtcNow;
}

public class DirectorModel
{
    public required string Name { get; set; }
}
