using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Models;

public class AccreditationApplicationModelTests
{
    private static AccreditationApplicationModel CreateModel(Guid? caseManagementWorkItemId) =>
        new()
        {
            OrganisationId = "org-123",
            Year = 2026,
            MaterialType = MaterialType.Plastic,
            CaseManagementWorkItemId = caseManagementWorkItemId,
        };

    // Regression guard: an unannotated Guid? property on this model serializes fine to JSON
    // but throws BsonSerializationException ("GuidRepresentation is Unspecified") the moment
    // Mongo actually tries to write a non-null value — a failure mode no unit test caught
    // until this one, because every prior test exercising Submit used a mocked persistence
    // layer rather than real BSON serialization.
    [Fact]
    public void CaseManagementWorkItemId_WithValue_SerializesToBsonWithoutThrowing()
    {
        var model = CreateModel(Guid.NewGuid());

        var act = () => model.ToBsonDocument();

        act.Should().NotThrow();
    }

    [Fact]
    public void CaseManagementWorkItemId_Null_SerializesToBsonWithoutThrowing()
    {
        var model = CreateModel(null);

        var act = () => model.ToBsonDocument();

        act.Should().NotThrow();
    }

    [Fact]
    public void CaseManagementWorkItemId_RoundTripsThroughBsonUnchanged()
    {
        var expected = Guid.NewGuid();
        var model = CreateModel(expected);

        var document = model.ToBsonDocument();
        var roundTripped = BsonSerializer.Deserialize<AccreditationApplicationModel>(document);

        roundTripped.CaseManagementWorkItemId.Should().Be(expected);
    }
}
