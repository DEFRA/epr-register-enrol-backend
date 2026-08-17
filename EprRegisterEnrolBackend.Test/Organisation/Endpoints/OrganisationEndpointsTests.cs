using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.Organisation.Models;
using EprRegisterEnrolBackend.Organisation.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.Organisation.Endpoints;

// Covers only the three routes with a live caller — see OrganisationEndpoints'
// header comment (the frontend's persistentStubApiClient write-through path).
public class OrganisationEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly IOrganisationPersistence _mockPersistence =
        Substitute.For<IOrganisationPersistence>();
    private readonly HttpClient _client;

    public OrganisationEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(_mockPersistence);
                });
            })
            .CreateClient();
    }

    [Fact]
    public async Task GetAll_ReturnsOrganisationsFromPersistence()
    {
        _mockPersistence
            .GetAllAsync()
            .Returns(
                new List<OrganisationSummaryModel>
                {
                    new()
                    {
                        OrgId = 1,
                        CompanyDetails = new CompanyDetailsModel { Name = "Test Org" },
                    },
                }
            );

        var result = await _client.GetFromJsonAsync<List<OrganisationSummaryModel>>(
            "/organisation"
        );

        result.Should().NotBeNull();
        result.Should().Contain(o => o.CompanyDetails!.Name == "Test Org");
    }

    [Fact]
    public async Task GetByOrgId_ReturnsOrganisation_WhenFound()
    {
        _mockPersistence
            .GetByOrgIdAsync(42)
            .Returns(new OrganisationModel { OrgId = 42, SchemaVersion = 1, Version = 1 });

        var response = await _client.GetAsync("/organisation/42");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetByOrgId_Returns404_WhenNotFound()
    {
        _mockPersistence.GetByOrgIdAsync(99).Returns((OrganisationModel?)null);

        var response = await _client.GetAsync("/organisation/99");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upsert_ReturnsOk_WhenValid()
    {
        _mockPersistence.UpsertAsync(Arg.Any<OrganisationModel>()).Returns(true);

        var response = await _client.PutAsJsonAsync(
            "/organisation/5/upsert",
            new OrganisationModel { OrgId = 5, SchemaVersion = 1, Version = 1 }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Upsert_ReturnsBadRequest_WhenInvalid()
    {
        // Upsert overwrites OrgId from the route, so it's SchemaVersion that must be
        // invalid here to reach the validator's rejection path.
        var response = await _client.PutAsJsonAsync(
            "/organisation/5/upsert",
            new OrganisationModel { OrgId = 5, SchemaVersion = 0, Version = 1 }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
