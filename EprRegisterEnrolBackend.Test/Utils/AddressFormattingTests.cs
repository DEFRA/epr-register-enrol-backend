using EprRegisterEnrolBackend.Utils;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.Utils;

public class AddressFormattingTests
{
    [Fact]
    public void Join_SkipsBlankParts() =>
        AddressFormatting.Join("1 Road", null, " ", "Town", "", "AB1 2CD").Should().Be("1 Road, Town, AB1 2CD");

    [Fact]
    public void Join_EveryPartBlank_IsEmpty() => AddressFormatting.Join(null, "", " ").Should().BeEmpty();
}
