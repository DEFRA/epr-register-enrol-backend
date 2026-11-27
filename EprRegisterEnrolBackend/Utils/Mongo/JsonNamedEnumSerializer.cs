using System.Reflection;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace EprRegisterEnrolBackend.Utils.Mongo;

/// <summary>
/// Stores an enum as each member's <see cref="JsonStringEnumMemberNameAttribute"/> name (as
/// <c>TonnageBandSerializer</c> does for tonnage bands), so Mongo and the API JSON can never disagree. A member without
/// the attribute is stored by its own name.
/// </summary>
public sealed class JsonNamedEnumSerializer<TEnum> : SerializerBase<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> s_names = Enum.GetValues<TEnum>()
        .ToDictionary(
            value => value,
            value =>
                typeof(TEnum)
                    .GetField(value.ToString())
                    ?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()
                    ?.Name ?? value.ToString()
        );

    private static readonly Dictionary<string, TEnum> s_values = s_names.ToDictionary(p => p.Value, p => p.Key);

    public override TEnum Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var name = context.Reader.ReadString();
        return s_values.TryGetValue(name, out var value)
            ? value
            : throw new FormatException($"'{name}' is not a {typeof(TEnum).Name}.");
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, TEnum value) =>
        context.Writer.WriteString(
            s_names.TryGetValue(value, out var name)
                ? name
                : throw new ArgumentOutOfRangeException(nameof(value), value, $"Not a {typeof(TEnum).Name}.")
        );
}
