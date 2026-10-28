using System.Text.Json;
using EprRegisterEnrolBackend.DomainModel.Entities;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.Test.DomainModel.Entities;

public class TonnageBandSerializerTests
{
    private sealed class Holder
    {
        // Named explicitly so the result does not depend on whether another test registered camelCase.
        [BsonElement("band")]
        [BsonSerializer(typeof(TonnageBandSerializer))]
        public TonnageBand? Band { get; set; }
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

    [Theory]
    [InlineData(TonnageBand.UpTo500, "up_to_500")]
    [InlineData(TonnageBand.UpTo5000, "up_to_5000")]
    [InlineData(TonnageBand.UpTo10000, "up_to_10000")]
    [InlineData(TonnageBand.Over10000, "over_10000")]
    public void JsonSerialize_EveryBand_WritesTheSameValueMongoStores(
        TonnageBand band,
        string stored
    )
    {
        var json = JsonSerializer.Serialize(band);

        json.Should().Be($"\"{stored}\"");
        JsonSerializer.Deserialize<TonnageBand>(json).Should().Be(band);
    }

    [Fact]
    public void Serialize_NoBand_StoresNullAndReadsItBack()
    {
        // Through the serializer itself: a globally registered ignore-if-null convention would otherwise
        // leave the element out before the serializer is reached.
        var document = new BsonDocument();
        using (var writer = new BsonDocumentWriter(document))
        {
            writer.WriteStartDocument();
            writer.WriteName("band");
            new TonnageBandSerializer().Serialize(
                BsonSerializationContext.CreateRoot(writer),
                new BsonSerializationArgs(),
                null
            );
            writer.WriteEndDocument();
        }

        document["band"].Should().Be(BsonNull.Value);
        BsonSerializer.Deserialize<Holder>(document).Band.Should().BeNull();
    }

    [Fact]
    public void Deserialize_UnknownValue_Throws()
    {
        var document = new BsonDocument("band", "up_to_a_lot");

        var read = () => BsonSerializer.Deserialize<Holder>(document);

        read.Should().Throw<FormatException>().WithMessage("*up_to_a_lot*");
    }
}
