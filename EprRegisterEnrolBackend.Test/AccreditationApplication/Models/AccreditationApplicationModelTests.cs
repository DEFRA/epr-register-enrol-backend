using System.Text.Json;
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

    [Theory]
    [InlineData(GlassRecyclingProcess.Remelt)]
    [InlineData(GlassRecyclingProcess.Other)]
    [InlineData(null)]
    public void GlassRecyclingProcess_RoundTripsThroughBsonUnchanged(
        GlassRecyclingProcess? glassRecyclingProcess
    )
    {
        var model = CreateModel(null);
        model.GlassRecyclingProcess = glassRecyclingProcess;

        var document = model.ToBsonDocument();
        var roundTripped = BsonSerializer.Deserialize<AccreditationApplicationModel>(document);

        roundTripped.GlassRecyclingProcess.Should().Be(glassRecyclingProcess);
    }

    [Theory]
    [InlineData(GlassRecyclingProcess.Remelt, "glass_re_melt")]
    [InlineData(GlassRecyclingProcess.Other, "glass_other")]
    public void GlassRecyclingProcess_SerializesToJsonAsSnakeCaseWireValue(
        GlassRecyclingProcess value,
        string expectedWireValue
    )
    {
        var json = JsonSerializer.Serialize(value);

        json.Should().Be($"\"{expectedWireValue}\"");
    }

    [Fact]
    public void Query_RoundTripsThroughBsonUnchanged()
    {
        var model = CreateModel(null);
        model.Query = new AccreditationApplicationQuery
        {
            QueryNote = "Please clarify tonnage.",
            QueriedSectionKeys = ["business-plan"],
            QuerySubmissions =
            [
                new QuerySubmission
                {
                    QuerySubmissionTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    SectionKeys = ["business-plan"],
                    QuerySubmitterContactDetails = new QuerySubmitterContactDetails
                    {
                        FullName = "Jane Doe",
                        Email = "jane@example.com",
                        Role = "Manager",
                    },
                },
            ],
        };

        var document = model.ToBsonDocument();
        var roundTripped = BsonSerializer.Deserialize<AccreditationApplicationModel>(document);

        roundTripped.Query.Should().NotBeNull();
        roundTripped.Query!.QueryNote.Should().Be("Please clarify tonnage.");
        roundTripped
            .Query.QueriedSectionKeys.Should()
            .ContainSingle()
            .Which.Should()
            .Be("business-plan");
        roundTripped.Query.QuerySubmissions.Should().ContainSingle();
        roundTripped
            .Query.QuerySubmissions[0]
            .QuerySubmitterContactDetails.FullName.Should()
            .Be("Jane Doe");
    }

    [Fact]
    public void AccreditationApplicationFile_LegacyDocumentMissingDocumentType_DeserializesToNull()
    {
        var model = CreateModel(null);
        model.SamplingPlan.Files.Add(
            new AccreditationApplicationFile
            {
                FileId = "legacy-1",
                Filename = "legacy.pdf",
                ContentType = "application/pdf",
                UploadedByUserId = "u1",
                S3Key = "sampling-plans/legacy-1",
                DocumentType = AccreditationFileDocumentType.SamplingPlan,
            }
        );
        var document = model.ToBsonDocument();

        // Simulate a Mongo sub-document written before DocumentType existed by stripping the
        // field entirely, rather than just setting it null — proves deserialization tolerates
        // the field being absent, not merely present-and-null.
        var samplingPlanDoc = document
            .Elements.Single(e => e.Name.Equals("SamplingPlan", StringComparison.OrdinalIgnoreCase))
            .Value.AsBsonDocument;
        var fileDoc = samplingPlanDoc
            .Elements.Single(e => e.Name.Equals("Files", StringComparison.OrdinalIgnoreCase))
            .Value.AsBsonArray[0]
            .AsBsonDocument;
        var documentTypeElementName = fileDoc
            .Elements.Single(e => e.Name.Equals("DocumentType", StringComparison.OrdinalIgnoreCase))
            .Name;
        fileDoc.Remove(documentTypeElementName);

        var act = () => BsonSerializer.Deserialize<AccreditationApplicationModel>(document);

        act.Should().NotThrow();
        act().SamplingPlan.Files.Single().DocumentType.Should().BeNull();
    }

    [Fact]
    public void SectionVersions_RoundTripThroughBson_ButAreNotSerializedToJson()
    {
        var model = CreateModel(null);
        model.Prns.Versions.Add(new PrnsSnapshot { VersionedAt = DateTime.UtcNow });

        var document = model.ToBsonDocument();
        var roundTripped = BsonSerializer.Deserialize<AccreditationApplicationModel>(document);
        roundTripped.Prns.Versions.Should().ContainSingle();

        var json = JsonSerializer.Serialize(model);
        json.Should().NotContain("Versions");
    }
}
