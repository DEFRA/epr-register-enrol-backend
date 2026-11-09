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
        "registrationEntityId",
        "accreditationEntityId",
        "overseasSiteEntityId",
    ];

    [Fact]
    public void ToBsonDocument_ForeignKeysNotStamped_OmitsThem()
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
    public void Deserialize_StampedForeignKeys_RoundTrips()
    {
        var registration = ObjectId.GenerateNewId();
        var accreditation = ObjectId.GenerateNewId();
        var site = ObjectId.GenerateNewId();
        var application = Application();
        application.RegistrationEntityId = registration;
        application.AccreditationEntityId = accreditation;
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    SiteName = "Site",
                    OverseasSiteEntityId = site,
                },
            ],
        };

        var read = BsonSerializer.Deserialize<AccreditationApplicationModel>(
            application.ToBsonDocument()
        );

        read.RegistrationEntityId.Should().Be(registration);
        read.AccreditationEntityId.Should().Be(accreditation);
        read.OverseasSites!.Sites.Single().OverseasSiteEntityId.Should().Be(site);
    }

    [Fact]
    public void Deserialize_DocumentStoredBeforeTheFields_ReadsThemAsNull()
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

        read.RegistrationEntityId.Should().BeNull();
        read.AccreditationEntityId.Should().BeNull();
        read.OverseasSites!.Sites.Single().OverseasSiteEntityId.Should().BeNull();
    }

    [Fact]
    public void JsonSerialize_StampedForeignKeys_LeavesThemOut()
    {
        var application = Application();
        application.RegistrationEntityId = ObjectId.GenerateNewId();
        application.AccreditationEntityId = ObjectId.GenerateNewId();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    SiteName = "Site",
                    OverseasSiteEntityId = ObjectId.GenerateNewId(),
                },
            ],
        };

        var json = JsonSerializer.Serialize(
            application,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );

        // Compare whole property names, so a longer name that contains one of these cannot match.
        using var document = JsonDocument.Parse(json);
        var site = document.RootElement.GetProperty("overseasSites").GetProperty("sites")[0];
        var names = document.RootElement.EnumerateObject().Concat(site.EnumerateObject()).Select(p => p.Name);
        names.Should().NotContain(s_newNames);
    }
}
