using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using EprRegisterEnrolBackend.Organisation.Models;
using EprRegisterEnrolBackend.Organisation.Services;

namespace EprRegisterEnrolBackend.Test.Organisation.Endpoints;

public class OrganisationEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public OrganisationEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IOrganisationPersistence, FakeOrganisationPersistence>();
            });
        }).CreateClient();
    }

    [Fact]
    public async Task GetAll_ReturnsSeededOrganisations()
    {
        var result = await _client.GetFromJsonAsync<List<OrganisationSummaryModel>>("/organisation");

        result.Should().NotBeNull();
        result.Should().Contain(o => o.CompanyDetails!.Name == "Operator Export Company");
    }
}
