using EprRegisterEnrolBackend.Config;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.Config;

public class TestOrganisationsConfigTests
{
    [Fact]
    public void Enabled_DefaultsToFalse()
    {
        new TestOrganisationsConfig().Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("PROD")]
    [InlineData(" prod ")]
    public void EnsureNotEnabledInProduction_EnabledInProd_Throws(string cdpEnvironment)
    {
        var act = () => TestOrganisationsConfig.EnsureNotEnabledInProduction(true, cdpEnvironment);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*TestOrganisations__Enabled*prod*");
    }

    [Theory]
    [InlineData("test")]
    [InlineData("dev")]
    [InlineData("perf-test")]
    [InlineData("production")]
    [InlineData("")]
    [InlineData(null)]
    public void EnsureNotEnabledInProduction_EnabledOutsideProd_DoesNotThrow(string? cdpEnvironment)
    {
        var act = () => TestOrganisationsConfig.EnsureNotEnabledInProduction(true, cdpEnvironment);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureNotEnabledInProduction_DisabledInProd_DoesNotThrow()
    {
        var act = () => TestOrganisationsConfig.EnsureNotEnabledInProduction(false, "prod");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("e2e-abc123", true)]
    [InlineData("e2e-", true)]
    [InlineData("E2E-abc123", false)]
    [InlineData("e2e_abc123", false)]
    [InlineData("e2eabc", false)]
    [InlineData("xe2e-abc", false)]
    [InlineData("50001", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTestOrganisation_MatchesOnlyTheLowercaseE2EPrefix(
        string? organisationId,
        bool expected
    )
    {
        TestOrganisationsConfig.IsTestOrganisation(organisationId).Should().Be(expected);
    }
}
