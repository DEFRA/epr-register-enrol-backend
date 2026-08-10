using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Models;

/// <summary>
/// RA-292 AC01/AC02. Applications are stored as POCOs in an
/// <c>IMongoCollection&lt;AccreditationApplicationModel&gt;</c>, so the C# property initializer —
/// not <c>default</c> — is what a stored document without the element comes back as. That makes
/// the initializer on <c>IsNewSite</c> load-bearing for the regulator's "new" badge, which is why
/// it is pinned here rather than left as an implementation detail of the model.
/// </summary>
public class OverseasSiteBsonDefaultsTests
{
    // The pre-RA-292 model shape, kept as a local type so the hazard stays demonstrable after the
    // real model was fixed.
    private class LegacyShapedSite
    {
        public int SiteId { get; set; }
        public string SiteName { get; set; } = "";
        public bool IsNewSite { get; set; } = true;
    }

    private static BsonDocument DocumentWithoutIsNewSite() =>
        new() { { "SiteId", 1 }, { "SiteName", "Legacy Site" } };

    [Fact]
    public void FieldInitializerSurvivesDeserialisation_WhichIsWhyTheDefaultMatters()
    {
        // Demonstrates the live defect the default flip fixed: under the old `= true` initializer,
        // every stored site predating the field came back flagged new. Missing elements do not
        // fall back to `default(bool)`.
        var deserialised = BsonSerializer.Deserialize<LegacyShapedSite>(
            DocumentWithoutIsNewSite()
        );

        deserialised.IsNewSite.Should().BeTrue();
    }

    [Fact]
    public void OverseasSite_StoredWithoutIsNewSite_IsNotFlaggedNew()
    {
        var deserialised = BsonSerializer.Deserialize<OverseasSiteModel>(
            DocumentWithoutIsNewSite()
        );

        deserialised.IsNewSite.Should().BeFalse("a legacy site must not arrive wearing a badge");
    }

    [Fact]
    public void InterimSite_StoredWithoutIsNewSite_IsNotFlaggedNew()
    {
        var document = new BsonDocument
        {
            { "SiteId", 2 },
            { "SiteNumber", "SN-0002" },
            { "Country", "France" },
            { "SiteName", "Interim" },
            { "AddressLine1", "1 Rue Example" },
            { "TownOrCity", "Paris" },
            { "ContactName", "Marie Curie" },
            { "ContactEmail", "marie@example.com" },
            { "ContactPhone", "0033111222333" },
        };

        var deserialised = BsonSerializer.Deserialize<InterimSiteModel>(document);

        deserialised.IsNewSite.Should().BeFalse();
    }

    [Fact]
    public void Authoriser_StoredWithoutIsNew_IsNotFlaggedNew()
    {
        var document = new BsonDocument
        {
            { "FullName", "Old Hand" },
            { "Email", "old@example.com" },
        };

        var deserialised = BsonSerializer.Deserialize<PrnsAuthoriser>(document);

        deserialised.IsNew.Should().BeFalse();
    }

    [Fact]
    public void StoredValuesAreStillHonoured()
    {
        var document = new BsonDocument
        {
            { "SiteId", 1 },
            { "SiteName", "Genuinely New Site" },
            { "IsNewSite", true },
        };

        BsonSerializer.Deserialize<OverseasSiteModel>(document).IsNewSite.Should().BeTrue();
    }
}
