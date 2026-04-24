using EprRegisterEnrolBackend.Organisation.Models;
using FluentAssertions;
using System.Net.Http.Json;

namespace EprRegisterEnrolBackend.Test.Organisation.Endpoints;

public class OrganisationTests : IClassFixture<TestApiFactory>
{
    private readonly HttpClient _client;

    public OrganisationTests(TestApiFactory factory)
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
