using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Models;

public class RegulatorNationMapperTests
{
    [Theory]
    [InlineData("ea", Nation.England)]
    [InlineData("EA", Nation.England)]
    [InlineData("nrw", Nation.Wales)]
    [InlineData("NRW", Nation.Wales)]
    [InlineData("sepa", Nation.Scotland)]
    [InlineData("SEPA", Nation.Scotland)]
    [InlineData("niea", Nation.NorthernIreland)]
    [InlineData("NIEA", Nation.NorthernIreland)]
    public void TryMap_RecognisedCode_ReturnsTrueAndMapsToExpectedNation(
        string code,
        Nation expected
    )
    {
        var result = RegulatorNationMapper.TryMap(code, out var nation);

        result.Should().BeTrue();
        nation.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryMap_NullOrBlankCode_ReturnsTrueAndDefaultsToEngland(string? code)
    {
        var result = RegulatorNationMapper.TryMap(code, out var nation);

        result
            .Should()
            .BeTrue(because: "a missing regulator code is the expected default, not a data gap");
        nation.Should().Be(Nation.England);
    }

    [Fact]
    public void TryMap_UnrecognisedCode_ReturnsFalseButStillDefaultsToEngland()
    {
        var result = RegulatorNationMapper.TryMap("not-a-real-regulator", out var nation);

        result
            .Should()
            .BeFalse(because: "an unrecognised code is a data gap the caller should warn about");
        nation
            .Should()
            .Be(Nation.England, because: "callers must still get a usable Nation, not a throw");
    }
}
