using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;

namespace EprRegisterEnrolBackend.Test.DomainModel.Entities;

public class TonnageBandConversionTests
{
    [Theory]
    [InlineData(PlannedTonnageBand.UpTo500, TonnageBand.UpTo500)]
    [InlineData(PlannedTonnageBand.UpTo5000, TonnageBand.UpTo5000)]
    [InlineData(PlannedTonnageBand.UpTo10000, TonnageBand.UpTo10000)]
    [InlineData(PlannedTonnageBand.Over10000, TonnageBand.Over10000)]
    public void FromLegacy_EveryBand_MapsToTheSameBand(
        PlannedTonnageBand legacy,
        TonnageBand expected
    ) => Assert.Equal(expected, TonnageBandConversion.FromLegacy(legacy));

    // The stored int32 values, so a reorder of the legacy enum would fail here.
    [Theory]
    [InlineData(0, TonnageBand.UpTo500)]
    [InlineData(1, TonnageBand.UpTo5000)]
    [InlineData(2, TonnageBand.UpTo10000)]
    [InlineData(3, TonnageBand.Over10000)]
    public void FromLegacy_StoredInt32_MapsToTheBand(int stored, TonnageBand expected) =>
        Assert.Equal(expected, TonnageBandConversion.FromLegacy((PlannedTonnageBand)stored));

    [Fact]
    public void FromLegacy_Null_IsNull() => Assert.Null(TonnageBandConversion.FromLegacy(null));

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void FromLegacy_OutOfRange_Throws(int stored) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TonnageBandConversion.FromLegacy((PlannedTonnageBand)stored)
        );

    [Theory]
    [InlineData(TonnageBand.UpTo500, "up_to_500")]
    [InlineData(TonnageBand.UpTo5000, "up_to_5000")]
    [InlineData(TonnageBand.UpTo10000, "up_to_10000")]
    [InlineData(TonnageBand.Over10000, "over_10000")]
    public void ReExName_EveryBand_RoundTrips(TonnageBand band, string name)
    {
        Assert.Equal(name, TonnageBandConversion.ToReExName(band));
        Assert.True(TonnageBandConversion.TryFromReExName(name, out var parsed));
        Assert.Equal(band, parsed);
    }

    [Fact]
    public void ToReExName_UndefinedBand_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TonnageBandConversion.ToReExName((TonnageBand)99)
        );

    [Fact]
    public void TryFromReExName_UnknownName_IsFalse() =>
        Assert.False(TonnageBandConversion.TryFromReExName("up_to_1", out _));
}
