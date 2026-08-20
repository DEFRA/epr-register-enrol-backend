using EprRegisterEnrolBackend.Utils;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.Utils;

public class CountryClassificationsTests
{
    [Fact]
    public void IsEu_EuCountry_ReturnsTrue()
    {
        CountryClassifications.IsEu("France").Should().BeTrue();
    }

    [Fact]
    public void IsEu_NonEuCountry_ReturnsFalse()
    {
        CountryClassifications.IsEu("China").Should().BeFalse();
    }

    [Fact]
    public void IsEu_CaseInsensitive_ReturnsTrue()
    {
        CountryClassifications.IsEu("france").Should().BeTrue();
    }

    [Fact]
    public void IsEu_Null_ReturnsFalse()
    {
        CountryClassifications.IsEu(null).Should().BeFalse();
    }

    [Fact]
    public void IsOecd_OecdCountry_ReturnsTrue()
    {
        CountryClassifications.IsOecd("Japan").Should().BeTrue();
    }

    [Fact]
    public void IsOecd_NonOecdCountry_ReturnsFalse()
    {
        CountryClassifications.IsOecd("Bulgaria").Should().BeFalse();
    }

    [Fact]
    public void IsOecd_EuAndOecdCountry_ReturnsTrue()
    {
        CountryClassifications.IsOecd("Germany").Should().BeTrue();
    }

    [Fact]
    public void IsOecd_Null_ReturnsFalse()
    {
        CountryClassifications.IsOecd(null).Should().BeFalse();
    }
}
