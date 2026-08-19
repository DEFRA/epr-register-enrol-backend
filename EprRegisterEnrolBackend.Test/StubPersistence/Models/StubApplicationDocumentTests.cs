using EprRegisterEnrolBackend.StubPersistence.Models;
using FluentAssertions;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.StubPersistence.Models;

public class StubApplicationDocumentTests
{
    [Fact]
    public void AllPropertiesRoundTrip()
    {
        var id = ObjectId.GenerateNewId();
        var updatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var data = new BsonDocument { { "key", "value" } };

        var document = new StubApplicationDocument
        {
            Id = id,
            StubApplicationId = "stub-app-1",
            OrganisationId = "org-1",
            SiteId = "site-1",
            MaterialType = "plastic",
            Year = 2026,
            Data = data,
            UpdatedAt = updatedAt,
        };

        document.Id.Should().Be(id);
        document.StubApplicationId.Should().Be("stub-app-1");
        document.OrganisationId.Should().Be("org-1");
        document.SiteId.Should().Be("site-1");
        document.MaterialType.Should().Be("plastic");
        document.Year.Should().Be(2026);
        document.Data.Should().BeEquivalentTo(data);
        document.UpdatedAt.Should().Be(updatedAt);
    }

    [Fact]
    public void DefaultValues_AreSensible()
    {
        var document = new StubApplicationDocument
        {
            StubApplicationId = "stub-app-2",
            OrganisationId = "org-2",
        };

        document.MaterialType.Should().Be(string.Empty);
        document.Data.Should().NotBeNull();
        document.Data.ElementCount.Should().Be(0);
        document.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }
}
