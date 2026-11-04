using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.Organisation.Services;
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

    // RA-425: org 50018 is the dedicated exporter DevExporterAccreditationSeedService accredits.
    // Anywhere that seed does not run, the stub ReEx registry must not have it, as before RA-425.
    private static async Task<bool> StubRegistryHasOrg50018(
        string environmentName,
        Dictionary<string, string?> settings,
        bool testOrganisationsEnabled = false
    )
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IReExClient>());
        services.AddReExApiAdapter(configuration, environment, testOrganisationsEnabled);

        var fakeOrgs = services
            .BuildServiceProvider()
            .GetRequiredService<FakeOrganisationPersistence>();
        return await fakeOrgs.GetByOrgIdAsync(50018) is not null;
    }

    [Theory]
    [InlineData(DevAccreditationHistorySeedService.ConfigKey)]
    [InlineData(DevExporterAccreditationSeedService.ConfigKey)]
    public async Task Development_WithTheExporterSeedOn_StubRegistryHasOrg50018(string seedFlag)
    {
        (await StubRegistryHasOrg50018(Environments.Development, new() { [seedFlag] = "true" }))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task Development_WithTheExporterSeedOff_StubRegistryHasNoOrg50018()
    {
        (await StubRegistryHasOrg50018(Environments.Development, [])).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OutsideDevelopment_StubRegistryHasNoOrg50018_WhateverTheSeedFlags(
        bool useStub,
        bool testOrganisationsEnabled
    )
    {
        var settings = new Dictionary<string, string?>
        {
            ["ReExApi:UseStub"] = useStub.ToString(),
            [DevAccreditationHistorySeedService.ConfigKey] = "true",
            [DevExporterAccreditationSeedService.ConfigKey] = "true",
        };

        (await StubRegistryHasOrg50018(Environments.Production, settings, testOrganisationsEnabled))
            .Should()
            .BeFalse();
    }
}
