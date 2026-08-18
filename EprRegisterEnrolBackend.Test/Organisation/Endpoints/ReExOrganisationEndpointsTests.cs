using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.Organisation.Endpoints;

public class ReExOrganisationEndpointsTests
    : IClassFixture<AccreditationApplicationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public ReExOrganisationEndpointsTests(AccreditationApplicationTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
    }

    [Fact]
    public async Task GetDefraLink_ReturnsLinkedId_WhenAdapterSucceeds()
    {
        _factory.MockReExAdapter
            .GetLinkedDefraOrganisationAsync("50002", Arg.Any<CancellationToken>())
            .Returns(
                ReExResult<LinkedDefraOrganisationResult>.Success(
                    new LinkedDefraOrganisationResult
                    {
                        OrganisationId = "50002",
                        LinkedDefraOrganisationId = "67b9e8fc-2235-431a-a7b9-80663c81b6ff",
                    },
                    200
                )
            );

        var response = await _client.GetAsync("/api/v1/organisations/50002/defra-link");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<LinkedDefraOrganisationResult>(
            JsonOptions
        );
        body!.OrganisationId.Should().Be("50002");
        body.LinkedDefraOrganisationId.Should().Be("67b9e8fc-2235-431a-a7b9-80663c81b6ff");
    }

    [Fact]
    public async Task GetDefraLink_ReturnsNullLink_WhenReExHasNoLink()
    {
        _factory.MockReExAdapter
            .GetLinkedDefraOrganisationAsync("50002", Arg.Any<CancellationToken>())
            .Returns(
                ReExResult<LinkedDefraOrganisationResult>.Success(
                    new LinkedDefraOrganisationResult
                    {
                        OrganisationId = "50002",
                        LinkedDefraOrganisationId = null,
                    },
                    200
                )
            );

        var response = await _client.GetAsync("/api/v1/organisations/50002/defra-link");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<LinkedDefraOrganisationResult>(
            JsonOptions
        );
        body!.LinkedDefraOrganisationId.Should().BeNull();
    }

    [Fact]
    public async Task GetDefraLink_Returns404_WhenOrganisationNotFound()
    {
        _factory.MockReExAdapter
            .GetLinkedDefraOrganisationAsync("99999", Arg.Any<CancellationToken>())
            .Returns(
                ReExResult<LinkedDefraOrganisationResult>.Fail(
                    new ReExError(ReExErrorKind.NotFound, "not found"),
                    404
                )
            );

        var response = await _client.GetAsync("/api/v1/organisations/99999/defra-link");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetDefraLink_ReturnsProblem_WhenReExUpstreamFails()
    {
        _factory.MockReExAdapter
            .GetLinkedDefraOrganisationAsync("50002", Arg.Any<CancellationToken>())
            .Returns(
                ReExResult<LinkedDefraOrganisationResult>.Fail(
                    new ReExError(ReExErrorKind.ServerError, "boom"),
                    502
                )
            );

        var response = await _client.GetAsync("/api/v1/organisations/50002/defra-link");

        ((int)response.StatusCode).Should().Be(502);
    }

    [Fact]
    public async Task GetDefraLink_ReturnsDefaultProblemMessage_WhenErrorMessageIsNull()
    {
        _factory.MockReExAdapter
            .GetLinkedDefraOrganisationAsync("50002", Arg.Any<CancellationToken>())
            .Returns(
                ReExResult<LinkedDefraOrganisationResult>.Fail(
                    new ReExError(ReExErrorKind.ServerError),
                    502
                )
            );

        var response = await _client.GetAsync("/api/v1/organisations/50002/defra-link");

        ((int)response.StatusCode).Should().Be(502);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Failed to resolve linked Defra organisation");
    }

    [Fact]
    public async Task GetDefraLink_DefaultsStatusCodeTo502_WhenResultHasNoStatusCode()
    {
        _factory.MockReExAdapter
            .GetLinkedDefraOrganisationAsync("50002", Arg.Any<CancellationToken>())
            .Returns(
                ReExResult<LinkedDefraOrganisationResult>.Fail(
                    new ReExError(ReExErrorKind.ServerError, "boom"),
                    null
                )
            );

        var response = await _client.GetAsync("/api/v1/organisations/50002/defra-link");

        ((int)response.StatusCode).Should().Be(502);
    }
}
