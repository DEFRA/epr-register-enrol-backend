using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// Stores a <see cref="TonnageBand"/> that is always present the same way as <see cref="TonnageBandSerializer"/>, as
/// ReEx's string. A stored null is refused, never read as a default band, because the type says the band is required.
/// </summary>
public sealed class RequiredTonnageBandSerializer : SerializerBase<TonnageBand>
{
    private static readonly TonnageBandSerializer s_band = new();

    public override TonnageBand Deserialize(
        BsonDeserializationContext context,
        BsonDeserializationArgs args
    ) =>
        s_band.Deserialize(context, args)
        ?? throw new FormatException("A required tonnage band is stored as null.");

    public override void Serialize(
        BsonSerializationContext context,
        BsonSerializationArgs args,
        TonnageBand value
    ) => s_band.Serialize(context, args, value);
}
