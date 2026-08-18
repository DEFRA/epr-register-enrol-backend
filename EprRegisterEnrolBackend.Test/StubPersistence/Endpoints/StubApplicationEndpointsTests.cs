using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.StubPersistence.Models;
using FluentAssertions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.StubPersistence.Endpoints;

public class StubApplicationEndpointsTests : IClassFixture<StubApplicationEndpointsTestFactory>
{
    private readonly StubApplicationEndpointsTestFactory _factory;
    private readonly HttpClient _client;

    public StubApplicationEndpointsTests(StubApplicationEndpointsTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetList_ReturnsJsonArrayOfStoredDocumentData()
    {
        var doc1 = new StubApplicationDocument
        {
            StubApplicationId = "app-1",
            OrganisationId = "org-1",
            Data = BsonDocument.Parse("""{"siteId":"S1"}"""),
        };
        var doc2 = new StubApplicationDocument
        {
            StubApplicationId = "app-2",
            OrganisationId = "org-1",
            Data = BsonDocument.Parse("""{"siteId":"S2"}"""),
        };
        _factory
            .MockPersistence.GetByOrgAsync("org-1")
            .Returns([doc1, doc2]);

        var response = await _client.GetAsync("/api/v1/stub/accreditation-applications/org-1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"S1\"");
        body.Should().Contain("\"S2\"");
    }

    [Fact]
    public async Task GetList_NoDocuments_ReturnsEmptyJsonArray()
    {
        _factory
            .MockPersistence.GetByOrgAsync("org-empty")
            .Returns(Enumerable.Empty<StubApplicationDocument>());

        var response = await _client.GetAsync(
            "/api/v1/stub/accreditation-applications/org-empty"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Trim().Should().Be("[]");
    }

    [Fact]
    public async Task GetById_ExistingDocument_ReturnsItsData()
    {
        var doc = new StubApplicationDocument
        {
            StubApplicationId = "app-3",
            OrganisationId = "org-2",
            Data = BsonDocument.Parse("""{"siteId":"S3"}"""),
        };
        _factory.MockPersistence.GetByIdAsync("org-2", "app-3").Returns(doc);

        var response = await _client.GetAsync(
            "/api/v1/stub/accreditation-applications/org-2/app-3"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"S3\"");
    }

    [Fact]
    public async Task GetById_UnknownDocument_ReturnsNotFound()
    {
        _factory
            .MockPersistence.GetByIdAsync("org-2", "does-not-exist")
            .Returns((StubApplicationDocument?)null);

        var response = await _client.GetAsync(
            "/api/v1/stub/accreditation-applications/org-2/does-not-exist"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upsert_ValidBody_CallsPersistenceAndReturnsNoContent()
    {
        var response = await _client.PutAsJsonAsync(
            "/api/v1/stub/accreditation-applications/org-3/app-4",
            new
            {
                siteId = "SITE-4",
                materialType = "plastic",
                year = 2026,
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await _factory
            .MockPersistence.Received(1)
            .UpsertAsync(
                Arg.Is<StubApplicationDocument>(d =>
                    d.OrganisationId == "org-3"
                    && d.StubApplicationId == "app-4"
                    && d.SiteId == "SITE-4"
                    && d.MaterialType == "plastic"
                    && d.Year == 2026
                )
            );
    }

    [Fact]
    public async Task Upsert_BodyWithoutOptionalFields_DefaultsMaterialTypeAndYear()
    {
        var response = await _client.PutAsJsonAsync(
            "/api/v1/stub/accreditation-applications/org-4/app-5",
            new { }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await _factory
            .MockPersistence.Received(1)
            .UpsertAsync(
                Arg.Is<StubApplicationDocument>(d =>
                    d.OrganisationId == "org-4"
                    && d.StubApplicationId == "app-5"
                    && d.SiteId == null
                    && d.MaterialType == string.Empty
                    && d.Year == 0
                )
            );
    }
}
