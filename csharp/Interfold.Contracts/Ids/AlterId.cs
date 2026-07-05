using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// Strongly-typed wrapper around the per-system integer alter id (backed by
/// <see cref="short"/> in Cassandra, exposed as <see cref="int"/> on the wire).
///
/// <para>See <see cref="SystemId"/> for the general story of the typed-id rollout.</para>
/// </summary>
[JsonConverter(typeof(AlterIdJsonConverter))]
public readonly record struct AlterId : IParsable<AlterId>
{
    public int Value { get; }

    public AlterId(int value)
    {
        Value = value;
    }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static AlterId Parse(string s, IFormatProvider? provider)
        => new(int.Parse(s, System.Globalization.NumberStyles.Integer, provider ?? System.Globalization.CultureInfo.InvariantCulture));

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out AlterId result)
    {
        if (int.TryParse(s, System.Globalization.NumberStyles.Integer, provider ?? System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            result = new AlterId(value);
            return true;
        }

        result = default;
        return false;
    }
}

internal sealed class AlterIdJsonConverter : JsonConverter<AlterId>
{
    public override AlterId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, AlterId value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);
}
