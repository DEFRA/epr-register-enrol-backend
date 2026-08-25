using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// RA-482: pure max+1/zero-pad allocation logic, scope-agnostic -- the caller decides which
// OrsId strings are "in scope" (current application only, or every application under a
// RegistrationId); this class only ever sees the flattened result.
public class OrsIdGeneratorTests
{
    [Fact]
    public void GenerateNext_EmptyScope_ReturnsFirstId()
    {
        var result = OrsIdGenerator.GenerateNext([]);

        result.CapacityExceeded.Should().BeFalse();
        result.OrsId.Should().Be("001");
    }

    [Fact]
    public void GenerateNext_SingleExistingId_ReturnsMaxPlusOne()
    {
        var result = OrsIdGenerator.GenerateNext(["003"]);

        result.OrsId.Should().Be("004");
    }

    [Fact]
    public void GenerateNext_MultipleExistingIds_ReturnsMaxAcrossAllPlusOne()
    {
        var result = OrsIdGenerator.GenerateNext(["001", "005", "003"]);

        result.OrsId.Should().Be("006");
    }

    [Fact]
    public void GenerateNext_IdsOutOfInsertionOrder_StillFindsTrueMax()
    {
        // Guards against an implementation that assumes the scope is pre-sorted or that the
        // last item is the max -- cross-application scans have no guaranteed ordering.
        var result = OrsIdGenerator.GenerateNext(["012", "045", "003"]);

        result.OrsId.Should().Be("046");
    }

    [Fact]
    public void GenerateNext_IgnoresNullAndNonNumericEntries()
    {
        // Deselected/legacy/malformed entries must not crash generation or be silently treated
        // as the max -- only entries that actually parse as a number count.
        var result = OrsIdGenerator.GenerateNext([null, "", "not-a-number", "002"]);

        result.OrsId.Should().Be("003");
    }

    [Theory]
    [InlineData(new[] { "5" }, "006")]
    [InlineData(new[] { "45" }, "046")]
    [InlineData(new[] { "9" }, "010")]
    public void GenerateNext_PadsToThreeDigits(string[] existing, string expected)
    {
        var result = OrsIdGenerator.GenerateNext(existing);

        result.OrsId.Should().Be(expected);
    }

    [Fact]
    public void GenerateNext_MaxIs998_ReturnsHighestValidId()
    {
        var result = OrsIdGenerator.GenerateNext(["998"]);

        result.CapacityExceeded.Should().BeFalse();
        result.OrsId.Should().Be("999");
    }

    [Fact]
    public void GenerateNext_MaxIs999_ReturnsCapacityExceeded()
    {
        var result = OrsIdGenerator.GenerateNext(["999"]);

        result.CapacityExceeded.Should().BeTrue();
        result.OrsId.Should().BeNull();
    }
}
