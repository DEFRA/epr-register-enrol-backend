using EprRegisterEnrolBackend.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Test.Auth;

public class ServiceClientRegistryTests
{
    private static ServiceClientRegistry Build(
        IEnumerable<ServiceClientDefinition> clients,
        Dictionary<string, string?>? settings = null
    ) =>
        new(
            Options.Create(new ServiceClientsConfig { Clients = clients.ToList() }),
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build(),
            NullLogger<ServiceClientRegistry>.Instance
        );

    [Fact]
    public void Client_IsFound_ByClientId_WithItsOwnSecret()
    {
        var registry = Build(
            [new ServiceClientDefinition { Name = "reex", ClientId = "reex-client" }],
            new() { ["AUTH_SHARED_SECRET:REEX"] = "reex-secret" }
        );

        registry.TryGet("reex-client", out var client).Should().BeTrue();
        client.Name.Should().Be("reex");
        client.Secrets.Should().Equal("reex-secret");
    }

    [Fact]
    public void EachClient_GetsOnlyItsOwnSecret()
    {
        var registry = Build(
            [
                new ServiceClientDefinition { Name = "reex", ClientId = "reex-client" },
                new ServiceClientDefinition { Name = "other-team", ClientId = "other-client" },
            ],
            new()
            {
                ["AUTH_SHARED_SECRET:REEX"] = "reex-secret",
                ["AUTH_SHARED_SECRET:OTHER_TEAM"] = "other-secret",
            }
        );

        registry.TryGet("reex-client", out var reex).Should().BeTrue();
        registry.TryGet("other-client", out var other).Should().BeTrue();
        reex.Secrets.Should().Equal("reex-secret");
        other.Secrets.Should().Equal("other-secret");
    }

    [Fact]
    public void NextSecret_IsAcceptedAlongsideTheCurrentOne_DuringRotation()
    {
        var registry = Build(
            [new ServiceClientDefinition { Name = "reex", ClientId = "reex-client" }],
            new()
            {
                ["AUTH_SHARED_SECRET:REEX"] = "current",
                ["AUTH_SHARED_SECRET:REEX_NEXT"] = "next",
            }
        );

        registry.TryGet("reex-client", out var client).Should().BeTrue();
        client.Secrets.Should().Equal("current", "next");
    }

    [Fact]
    public void ClientWithNoSecret_IsLeftOut_SoItsRequestsFailClosed()
    {
        var registry = Build(
            [new ServiceClientDefinition { Name = "reex", ClientId = "reex-client" }]
        );

        registry.TryGet("reex-client", out _).Should().BeFalse();
        registry.All.Should().BeEmpty();
    }

    [Fact]
    public void UnknownClientId_IsNotFound()
    {
        var registry = Build(
            [new ServiceClientDefinition { Name = "reex", ClientId = "reex-client" }],
            new() { ["AUTH_SHARED_SECRET:REEX"] = "reex-secret" }
        );

        registry.TryGet("somebody-else", out _).Should().BeFalse();
    }

    [Fact]
    public void ClientIdLookup_IsCaseSensitive()
    {
        var registry = Build(
            [new ServiceClientDefinition { Name = "reex", ClientId = "reex-client" }],
            new() { ["AUTH_SHARED_SECRET:REEX"] = "reex-secret" }
        );

        registry.TryGet("REEX-CLIENT", out _).Should().BeFalse();
    }

    [Fact]
    public void DuplicateClientId_StopsStartup()
    {
        var act = () =>
            Build(
                [
                    new ServiceClientDefinition { Name = "reex", ClientId = "same" },
                    new ServiceClientDefinition { Name = "other", ClientId = "same" },
                ]
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*same*");
    }

    [Theory]
    [InlineData("", "id")]
    [InlineData("name", "")]
    [InlineData(" ", " ")]
    public void ClientWithoutANameOrId_StopsStartup(string name, string clientId)
    {
        var act = () =>
            Build([new ServiceClientDefinition { Name = name, ClientId = clientId }]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("reex", "REEX")]
    [InlineData("other-team", "OTHER_TEAM")]
    [InlineData(" ReEx ", "REEX")]
    public void SecretName_IsTheUpperCaseClientName(string name, string expected) =>
        ServiceClientRegistry.SecretConfigName(name).Should().Be(expected);
}
