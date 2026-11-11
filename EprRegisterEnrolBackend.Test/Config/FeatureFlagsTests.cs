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
}
