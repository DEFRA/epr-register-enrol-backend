using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Utils.Mongo;
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
    // Whether a hand-built BsonDocument's element names need to be camelCase, and whether an
    // unmatched element throws or is silently ignored, depends entirely on whether
    // MongoDbClientFactory's CamelCaseElementNameConvention has been registered yet —
    // registration is process-global and normally only happens as a side effect of some other
    // test constructing a WebApplicationFactory. Calling it explicitly here makes every test in
    // this class deterministic regardless of xUnit's test-class ordering/parallelisation,
    // instead of depending on which other test class happened to run first.
    static OverseasSiteBsonDefaultsTests()
    {
        MongoDbClientFactory.EnsureConventionRegistered();
    }

    // The pre-RA-292 model shape, kept as a local type so the hazard stays demonstrable after the
    // real model was fixed.
    private class LegacyShapedSite
    {
        public int SiteId { get; set; }
        public string SiteName { get; set; } = "";
        public bool IsNewSite { get; set; } = true;
    }

    private static BsonDocument DocumentWithoutIsNewSite() =>
        new() { { "siteId", 1 }, { "siteName", "Legacy Site" } };

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
            { "siteId", 2 },
            { "siteNumber", "SN-0002" },
            { "country", "France" },
            { "siteName", "Interim" },
            { "addressLine1", "1 Rue Example" },
            { "townOrCity", "Paris" },
            { "contactName", "Marie Curie" },
            { "contactEmail", "marie@example.com" },
            { "contactPhone", "0033111222333" },
        };

        var deserialised = BsonSerializer.Deserialize<InterimSiteModel>(document);

        deserialised.IsNewSite.Should().BeFalse();
    }

    [Fact]
    public void Authoriser_StoredWithoutIsNew_IsNotFlaggedNew()
    {
        var document = new BsonDocument
        {
            { "fullName", "Old Hand" },
            { "email", "old@example.com" },
        };

        var deserialised = BsonSerializer.Deserialize<PrnsAuthoriser>(document);

        deserialised.IsNew.Should().BeFalse();
    }

    [Fact]
    public void OverseasSite_StoredWithoutRegisteredNowAccredited_IsNotPromoted()
    {
        // epr-zgrb: confirmed rather than assumed. The initializer is already `= false`, so unlike
        // IsNewSite this one never had the hazard — but it is the same class of thing, so it is
        // pinned here so a future edit to a truthy default fails loudly.
        var deserialised = BsonSerializer.Deserialize<OverseasSiteModel>(
            DocumentWithoutIsNewSite()
        );

        deserialised.RegisteredNowAccredited.Should().BeFalse();
    }

    [Fact]
    public void OverseasSite_StoredWithoutSelected_DefaultsToSelected()
    {
        // Documents the one remaining `= true` initializer on this model. Unlike IsNewSite this is
        // NOT a defect: Selected is operator-owned journey state, not regulator-facing, and the
        // frontend legitimately sets it. Pinned so the asymmetry is a recorded decision rather
        // than something a later reader "fixes" for consistency and changes journey behaviour.
        var deserialised = BsonSerializer.Deserialize<OverseasSiteModel>(
            DocumentWithoutIsNewSite()
        );

        deserialised.Selected.Should().BeTrue();
    }

    [Fact]
    public void StoredValuesAreStillHonoured()
    {
        // Element names must be camelCase, matching MongoDbClientFactory's
        // CamelCaseElementNameConvention (real stored documents are camelCase). Every other
        // test in this file only checks a *missing*-element default, so casing doesn't affect
        // them either way — this is the one test that reads a present element back, and a
        // PascalCase name here silently fails to match the class map once that convention is
        // registered (IgnoreExtraElementsConvention swallows the mismatch rather than
        // erroring), leaving IsNewSite at its unrelated default instead of the stored value.
        // That made this test's pass/fail depend on whether it happened to run before or after
        // any test that constructs MongoDbClientFactory — not a race, but an ordering bug with
        // the same symptom.
        var document = new BsonDocument
        {
            { "siteId", 1 },
            { "siteName", "Genuinely New Site" },
            { "isNewSite", true },
        };

        BsonSerializer.Deserialize<OverseasSiteModel>(document).IsNewSite.Should().BeTrue();
    }
}
