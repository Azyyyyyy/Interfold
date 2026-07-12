using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// A display color for alters, tags, and journals — canonically a <c>#RRGGBB</c> hex string.
/// Construction does NOT validate: legacy rows and client-authored payloads may carry
/// arbitrary spellings, and the persisted command JSON (idempotency-hashed) must round-trip
/// verbatim. Use <see cref="IsWellFormed"/> where a format check is wanted. JSON serializes
/// as the raw string.
/// </summary>
[JsonConverter(typeof(HexColorJsonConverter))]
public readonly record struct HexColor
{
    public string Value { get; }

    public HexColor(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(HexColor)raw</c> instead of
    /// <c>new HexColor(raw)</c>. See <see cref="SystemId"/> for the wider rationale.
    /// </summary>
    public static explicit operator HexColor(string value) => new(value);

    /// <summary>
    /// Implicit widen to the raw <see cref="string"/> for wire / DB boundary use.
    /// </summary>
    public static implicit operator string(HexColor value) => value.Value;

    /// <summary>True when the value matches <c>#RGB</c>, <c>#RRGGBB</c>, or <c>#RRGGBBAA</c>.</summary>
    public bool IsWellFormed
    {
        get
        {
            if (string.IsNullOrEmpty(Value) || Value[0] != '#')
                return false;

            var digits = Value.Length - 1;
            if (digits is not (3 or 6 or 8))
                return false;

            for (var i = 1; i < Value.Length; i++)
            {
                if (!Uri.IsHexDigit(Value[i]))
                    return false;
            }

            return true;
        }
    }

    public override string ToString() => Value;

    /// <summary>Null-preserving wrap for DB/state reads.</summary>
    public static HexColor? FromNullable(string? value) => value is null ? null : new HexColor(value);
}

internal sealed class HexColorJsonConverter : JsonConverter<HexColor>
{
    public override HexColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, HexColor value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
