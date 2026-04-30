using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using EprRegisterEnrolBackend.Organisation.Models;

namespace EprRegisterEnrolBackend.Test.Organisation.Endpoints;

public class OrganisationEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public OrganisationEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetAll_ReturnsSeededOrganisations()
    {
        var result = await _client.GetFromJsonAsync<List<OrganisationModel>>("/organisation", cancellationToken: TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Should().Contain(o => o.CompanyName == "Operator Export Company");
    }
}
