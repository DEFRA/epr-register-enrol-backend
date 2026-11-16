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

    // RA-000: no default nation. ReEx's own schema requires one of the four codes, so a null,
    // blank or unknown code is a ReEx defect the caller fails on.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-regulator")]
    public void TryMap_NullBlankOrUnrecognisedCode_ReturnsFalse(string? code) =>
        RegulatorNationMapper.TryMap(code, out _).Should().BeFalse();
}
