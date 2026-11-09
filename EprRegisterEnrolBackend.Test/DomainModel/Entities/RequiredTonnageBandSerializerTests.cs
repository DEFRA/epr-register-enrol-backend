using EprRegisterEnrolBackend.DomainModel.Entities;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.Test.DomainModel.Entities;

/// <summary>
/// A tonnage band that must always be present, such as the band a tonnage change asks for: stored as ReEx's value like
/// every other band, and never read back from a missing or null value as a default band.
/// </summary>
public class RequiredTonnageBandSerializerTests
{
    private sealed class Holder
    {
        // Named explicitly so the result does not depend on whether another test registered camelCase.
        [BsonElement("band")]
        [BsonSerializer(typeof(RequiredTonnageBandSerializer))]
        public TonnageBand Band { get; set; }
    }

    [Theory]
    [InlineData(TonnageBand.UpTo500, "up_to_500")]
    [InlineData(TonnageBand.UpTo5000, "up_to_5000")]
    [InlineData(TonnageBand.UpTo10000, "up_to_10000")]
    [InlineData(TonnageBand.Over10000, "over_10000")]
    public void Serialize_EveryBand_StoresReExsValueAndReadsItBack(TonnageBand band, string stored)
    {
        var document = new Holder { Band = band }.ToBsonDocument();

        document["band"].Should().Be((BsonValue)stored);
        BsonSerializer.Deserialize<Holder>(document).Band.Should().Be(band);
    }

    [Fact]
    public void Deserialize_StoredNull_IsRefusedRatherThanReadAsADefaultBand()
    {
        var act = () =>
            BsonSerializer.Deserialize<Holder>(new BsonDocument("band", BsonNull.Value));

        act.Should().Throw<Exception>().WithInnerExceptionExactly<FormatException>();
    }
}
