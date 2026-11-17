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

    // RA-705: ReadFromModel serves every read from the domain model, which on CDP only dual-write keeps current, only
    // seed dual-write starts (the initial Application version) and Expand creates. Off by default, so deploying changes
    // nothing until an environment opts in.
    private static IConfiguration ReadSettings(
        bool readFromModel,
        bool dualWrite,
        bool dualWriteSeed,
        bool expand
    ) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [FeatureFlags.ReadFromModelKey] = readFromModel ? "true" : "false",
                    ["DomainModel:DualWrite"] = dualWrite ? "true" : "false",
                    ["DomainModel:DualWriteSeed"] = dualWriteSeed ? "true" : "false",
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
    [InlineData(false, true, true, "DomainModel:DualWrite")]
    [InlineData(true, false, true, "DomainModel:DualWriteSeed")]
    [InlineData(true, true, false, "DomainModel:ExpandEnabled")]
    [InlineData(false, false, false, "DomainModel:DualWrite")]
    public void ReadFromModelWithoutItsPrerequisites_OutsideDevelopment_RefusesToStart(
        bool dualWrite,
        bool dualWriteSeed,
        bool expand,
        string missing
    )
    {
        var act = () =>
            FeatureFlags.EnsureReadFromModelHasItsPrerequisites(
                ReadSettings(readFromModel: true, dualWrite, dualWriteSeed, expand),
                new FakeHostEnvironment(Environments.Production)
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage($"DomainModel:ReadFromModel needs {missing}*");
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, false, true)]
    public void ReadFromModelOtherCombinations_OutsideDevelopment_Start(
        bool readFromModel,
        bool dualWrite,
        bool dualWriteSeed,
        bool expand
    )
    {
        var act = () =>
            FeatureFlags.EnsureReadFromModelHasItsPrerequisites(
                ReadSettings(readFromModel, dualWrite, dualWriteSeed, expand),
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
                ReadSettings(
                    readFromModel: true,
                    dualWrite: false,
                    dualWriteSeed: false,
                    expand: false
                ),
                new FakeHostEnvironment(Environments.Development)
            );

        act.Should().NotThrow();
    }

    // RA-706: LegacyWrites off is the cut: the legacy collection is no longer written and the domain model is the only
    // source of truth. On by default, so a missing setting can never cut. Off needs every read served from the model
    // and every write reaching it, in every environment: a frozen legacy read, or a write that reaches neither store,
    // is wrong locally too.
    private static IConfiguration CutSettings(
        bool legacyWrites,
        bool readFromModel,
        bool dualWrite,
        bool dualWriteSeed,
        bool expand
    ) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [FeatureFlags.LegacyWritesKey] = legacyWrites ? "true" : "false",
                    [FeatureFlags.ReadFromModelKey] = readFromModel ? "true" : "false",
                    ["DomainModel:DualWrite"] = dualWrite ? "true" : "false",
                    ["DomainModel:DualWriteSeed"] = dualWriteSeed ? "true" : "false",
                    ["DomainModel:ExpandEnabled"] = expand ? "true" : "false",
                }
            )
            .Build();

    [Fact]
    public void LegacyWritesKey_IsUnderTheDomainModelSection() =>
        FeatureFlags.LegacyWritesKey.Should().Be("DomainModel:LegacyWrites");

    [Fact]
    public void LegacyWrites_IsOnByDefault() =>
        new EprRegisterEnrolBackend.DomainModel.DomainModelConfig().LegacyWrites.Should().BeTrue();

    [Fact]
    public void LegacyWritesUnset_StartsWithNoOtherFlags()
    {
        var act = () =>
            FeatureFlags.EnsureLegacyWritesCutHasItsPrerequisites(
                new ConfigurationBuilder().Build()
            );

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(false, true, true, true, "DomainModel:ReadFromModel")]
    [InlineData(true, false, true, true, "DomainModel:DualWrite")]
    [InlineData(true, true, false, true, "DomainModel:DualWriteSeed")]
    [InlineData(true, true, true, false, "DomainModel:ExpandEnabled")]
    [InlineData(false, false, false, false, "DomainModel:ReadFromModel")]
    public void LegacyWritesOffWithoutItsPrerequisites_RefusesToStart(
        bool readFromModel,
        bool dualWrite,
        bool dualWriteSeed,
        bool expand,
        string missing
    )
    {
        var act = () =>
            FeatureFlags.EnsureLegacyWritesCutHasItsPrerequisites(
                CutSettings(legacyWrites: false, readFromModel, dualWrite, dualWriteSeed, expand)
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage($"DomainModel:LegacyWrites off needs {missing}*");
    }

    // The in-year changes routes still read the legacy collection, which the cut freezes; the in-year work moves them
    // to the model (decided 2026-11-15). Until then the two can't be on together.
    private static IConfiguration CutWithInYearChanges(bool legacyWrites, bool inYearChanges) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [FeatureFlags.LegacyWritesKey] = legacyWrites ? "true" : "false",
                    [FeatureFlags.ReadFromModelKey] = "true",
                    ["DomainModel:DualWrite"] = "true",
                    ["DomainModel:DualWriteSeed"] = "true",
                    ["DomainModel:ExpandEnabled"] = "true",
                    [FeatureFlags.InYearChangesEnabledKey] = inYearChanges ? "true" : "false",
                }
            )
            .Build();

    [Fact]
    public void LegacyWritesOffWithInYearChangesOn_RefusesToStart()
    {
        var act = () =>
            FeatureFlags.EnsureLegacyWritesCutHasItsPrerequisites(
                CutWithInYearChanges(legacyWrites: false, inYearChanges: true)
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "DomainModel:LegacyWrites off needs FeatureFlags:InYearChangesEnabled off*"
            );
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void LegacyWritesWithInYearChanges_OtherCombinations_Start(
        bool legacyWrites,
        bool inYearChanges
    )
    {
        var act = () =>
            FeatureFlags.EnsureLegacyWritesCutHasItsPrerequisites(
                CutWithInYearChanges(legacyWrites, inYearChanges)
            );

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(false, true, true, true, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, true, true, true)]
    public void LegacyWritesOtherCombinations_Start(
        bool legacyWrites,
        bool readFromModel,
        bool dualWrite,
        bool dualWriteSeed,
        bool expand
    )
    {
        var act = () =>
            FeatureFlags.EnsureLegacyWritesCutHasItsPrerequisites(
                CutSettings(legacyWrites, readFromModel, dualWrite, dualWriteSeed, expand)
            );

        act.Should().NotThrow();
    }
}
