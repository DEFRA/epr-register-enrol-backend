using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.ReEx;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

public class ReExApiAdapterRegistrationTests
{
    private static IReExApiAdapter Resolve(
        string environmentName,
        bool testOrganisationsEnabled,
        bool useStub = false
    )
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["ReExApi:UseStub"] = useStub.ToString() }
            )
            .Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IReExClient>());
        services.AddReExApiAdapter(configuration, environment, testOrganisationsEnabled);

        return services.BuildServiceProvider().GetRequiredService<IReExApiAdapter>();
    }

    [Fact]
    public void OutsideDevelopment_FlagOff_RegistersRealAdapterOnly()
    {
        Resolve(Environments.Production, testOrganisationsEnabled: false)
            .Should()
            .BeOfType<HttpReExApiAdapter>();
    }

    [Fact]
    public void OutsideDevelopment_FlagOn_RegistersTestOrganisationComposite()
    {
        Resolve(Environments.Production, testOrganisationsEnabled: true)
            .Should()
            .BeOfType<TestOrganisationReExApiAdapter>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Development_RegistersStubForEverything_WhateverTheFlag(bool enabled)
    {
        Resolve(Environments.Development, enabled).Should().BeOfType<StubReExApiAdapter>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UseStub_RegistersStubForEverything_WhateverTheFlag(bool enabled)
    {
        Resolve(Environments.Production, enabled, useStub: true)
            .Should()
            .BeOfType<StubReExApiAdapter>();
    }

    [Fact]
    public async Task FlagOn_TestOrganisation_IsAnsweredByTheStub()
    {
        var adapter = Resolve(Environments.Production, testOrganisationsEnabled: true);

        var result = await adapter.GetAccreditationAsync(
            "e2e-feedface",
            "e2e-reg-feedface",
            MaterialType.Plastic,
            2025
        );

        result.IsSuccess.Should().BeTrue();
        result.Value!.OrganisationName.Should().Be("E2E Organisation e2e-feedface");
    }
}
