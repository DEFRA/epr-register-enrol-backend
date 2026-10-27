using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Models;

/// <summary>
/// B5: the legacy application gains foreign keys to the domain model. The existing endpoints return the stored
/// document verbatim and legacy readers know nothing of these fields, so they must be invisible on the wire and
/// absent from stored documents until stamped.
/// </summary>
public class DomainModelForeignKeyTests
{
    static DomainModelForeignKeyTests()
    {
        MongoDbClientFactory.EnsureConventionRegistered();
    }

    private static AccreditationApplicationModel Application() =>
        new()
        {
            OrganisationId = "org-1",
            Year = 2027,
            MaterialType = MaterialType.Plastic,
        };

    private static readonly string[] s_newNames =
    [
        "registrationKey",
        "accreditationId",
        "overseasSiteId",
    ];

    [Fact]
    public void Unstamped_foreign_keys_are_not_written_to_stored_documents()
    {
        var application = Application();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site" }],
        };

        var stored = application.ToBsonDocument().ToJson();

        foreach (var name in s_newNames)
        {
            stored.Should().NotContain(name);
        }
    }

    [Fact]
    public void Stamped_foreign_keys_round_trip_through_storage()
    {
        var registration = ObjectId.GenerateNewId();
        var accreditation = ObjectId.GenerateNewId();
        var site = ObjectId.GenerateNewId();
        var application = Application();
        application.RegistrationKey = registration;
        application.AccreditationId = accreditation;
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    SiteName = "Site",
                    OverseasSiteId = site,
                },
            ],
        };

        var read = BsonSerializer.Deserialize<AccreditationApplicationModel>(
            application.ToBsonDocument()
        );

        read.RegistrationKey.Should().Be(registration);
        read.AccreditationId.Should().Be(accreditation);
        read.OverseasSites!.Sites.Single().OverseasSiteId.Should().Be(site);
    }

    [Fact]
    public void A_document_stored_before_the_fields_existed_still_reads()
    {
        var legacy = new BsonDocument
        {
            { "organisationId", "org-1" },
            { "year", 2027 },
            { "materialType", "Plastic" },
            {
                "overseasSites",
                new BsonDocument(
                    "sites",
                    new BsonArray
                    {
                        new BsonDocument { { "siteId", 1 }, { "siteName", "Site" } },
                    }
                )
            },
        };

        var read = BsonSerializer.Deserialize<AccreditationApplicationModel>(legacy);

        read.RegistrationKey.Should().BeNull();
        read.AccreditationId.Should().BeNull();
        read.OverseasSites!.Sites.Single().OverseasSiteId.Should().BeNull();
    }

    [Fact]
    public void The_foreign_keys_never_appear_in_the_json_the_endpoints_return()
    {
        var application = Application();
        application.RegistrationKey = ObjectId.GenerateNewId();
        application.AccreditationId = ObjectId.GenerateNewId();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    SiteName = "Site",
                    OverseasSiteId = ObjectId.GenerateNewId(),
                },
            ],
        };

        var json = JsonSerializer.Serialize(
            application,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );

        // Compare whole property names: "sourceReExAccreditationId" legitimately contains "accreditationId".
        using var document = JsonDocument.Parse(json);
        var site = document.RootElement.GetProperty("overseasSites").GetProperty("sites")[0];
        var names = document.RootElement.EnumerateObject().Concat(site.EnumerateObject()).Select(p => p.Name);
        names.Should().NotContain(s_newNames);
    }
}
