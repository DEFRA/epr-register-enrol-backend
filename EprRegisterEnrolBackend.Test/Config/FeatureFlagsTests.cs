using EprRegisterEnrolBackend.Config;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolBackend.Test.Config;

public class FeatureFlagsTests
{
    private static IConfiguration Settings(bool inYearChanges, bool dualWrite) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [FeatureFlags.InYearChangesEnabledKey] = inYearChanges ? "true" : "false",
                    ["DomainModel:DualWrite"] = dualWrite ? "true" : "false",
                }
            )
            .Build();

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void InYearChangesWithoutDualWrite_OutsideDevelopment_RefusesToStart()
    {
        var act = () =>
            FeatureFlags.EnsureInYearChangesHasDualWrite(
                Settings(inYearChanges: true, dualWrite: false),
                new FakeHostEnvironment(Environments.Production)
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("FeatureFlags:InYearChangesEnabled needs DomainModel:DualWrite*");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void OtherCombinations_OutsideDevelopment_Start(bool inYearChanges, bool dualWrite)
    {
        var act = () =>
            FeatureFlags.EnsureInYearChangesHasDualWrite(
                Settings(inYearChanges, dualWrite),
                new FakeHostEnvironment(Environments.Production)
            );

        act.Should().NotThrow();
    }

    // Locally and in the fe-tests stack the dev seeds write the domain model directly.
    [Fact]
    public void InYearChangesWithoutDualWrite_InDevelopment_Starts()
    {
        var act = () =>
            FeatureFlags.EnsureInYearChangesHasDualWrite(
                Settings(inYearChanges: true, dualWrite: false),
                new FakeHostEnvironment(Environments.Development)
            );

        act.Should().NotThrow();
    }

    // RA-705: ReadFromModel serves every read from the domain model, which on CDP only dual-write keeps current
    // (and Expand creates). Off by default, so deploying changes nothing until an environment opts in.
    private static IConfiguration ReadSettings(bool readFromModel, bool dualWrite, bool expand) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [FeatureFlags.ReadFromModelKey] = readFromModel ? "true" : "false",
                    ["DomainModel:DualWrite"] = dualWrite ? "true" : "false",
                    ["DomainModel:ExpandEnabled"] = expand ? "true" : "false",
                }
            )
            .Build();

    [Fact]
    public void ReadFromModelKey_IsUnderTheDomainModelSection() =>
        FeatureFlags.ReadFromModelKey.Should().Be("DomainModel:ReadFromModel");

    [Fact]
    public void ReadFromModel_IsOffByDefault() =>
        new EprRegisterEnrolBackend.DomainModel.DomainModelConfig()
            .ReadFromModel.Should()
            .BeFalse();

    [Theory]
    [InlineData(false, true, "DomainModel:DualWrite")]
    [InlineData(true, false, "DomainModel:ExpandEnabled")]
    [InlineData(false, false, "DomainModel:DualWrite")]
    public void ReadFromModelWithoutItsPrerequisites_OutsideDevelopment_RefusesToStart(
        bool dualWrite,
        bool expand,
        string missing
    )
    {
        var act = () =>
            FeatureFlags.EnsureReadFromModelHasItsPrerequisites(
                ReadSettings(readFromModel: true, dualWrite, expand),
                new FakeHostEnvironment(Environments.Production)
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage($"DomainModel:ReadFromModel needs {missing}*");
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public void ReadFromModelOtherCombinations_OutsideDevelopment_Start(
        bool readFromModel,
        bool dualWrite,
        bool expand
    )
    {
        var act = () =>
            FeatureFlags.EnsureReadFromModelHasItsPrerequisites(
                ReadSettings(readFromModel, dualWrite, expand),
                new FakeHostEnvironment(Environments.Production)
            );

        act.Should().NotThrow();
    }

    // Locally and in the fe-tests stack the dev seeds write the domain model directly.
    [Fact]
    public void ReadFromModelWithoutDualWrite_InDevelopment_Starts()
    {
        var act = () =>
            FeatureFlags.EnsureReadFromModelHasItsPrerequisites(
                ReadSettings(readFromModel: true, dualWrite: false, expand: false),
                new FakeHostEnvironment(Environments.Development)
            );

        act.Should().NotThrow();
    }
}
