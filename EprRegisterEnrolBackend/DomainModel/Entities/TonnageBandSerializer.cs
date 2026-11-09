using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// Stores a <see cref="TonnageBand"/> as ReEx's string (<c>up_to_500</c>, <c>up_to_5000</c>, <c>up_to_10000</c>,
/// <c>over_10000</c>), which is the stored value the design keeps (D2), rather than the C# member name.
/// </summary>
public sealed class TonnageBandSerializer : SerializerBase<TonnageBand?>
{
    public override TonnageBand? Deserialize(
        BsonDeserializationContext context,
        BsonDeserializationArgs args
    )
    {
        var reader = context.Reader;
        if (reader.GetCurrentBsonType() == BsonType.Null)
        {
            reader.ReadNull();
            return null;
        }

        var value = reader.ReadString();
        return TonnageBandConversion.TryFromReExName(value, out var band)
            ? band
            : throw new FormatException($"'{value}' is not a ReEx tonnage band.");
    }

    public override void Serialize(
        BsonSerializationContext context,
        BsonSerializationArgs args,
        TonnageBand? value
    )
    {
        if (value is null)
        {
            context.Writer.WriteNull();
        }
        else
        {
            context.Writer.WriteString(TonnageBandConversion.ToReExName(value.Value));
        }
    }
}
