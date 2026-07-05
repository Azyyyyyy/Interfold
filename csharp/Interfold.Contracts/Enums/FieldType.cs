using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Type of a user-defined settings field. The wire representation is the lowercase
/// snake_case name (<c>"text"</c>, <c>"month_year"</c>, <c>"month_day"</c>, ...),
/// preserving what the Elixir server emitted so the Kotlin client and any prior
/// captured payloads deserialize unchanged.
/// </summary>
[JsonConverter(typeof(FieldTypeJsonConverter))]
public enum FieldType
{
    Text,
    Number,
    Boolean,
    Date,
    Colour,
    Plaintext,
    Month,
    Year,
    MonthYear,
    Timestamp,
    MonthDay,
}

public static class FieldTypeExtensions
{
    public static string ToWireValue(this FieldType type) => type switch
    {
        FieldType.Text => "text",
        FieldType.Number => "number",
        FieldType.Boolean => "boolean",
        FieldType.Date => "date",
        FieldType.Colour => "colour",
        FieldType.Plaintext => "plaintext",
        FieldType.Month => "month",
        FieldType.Year => "year",
        FieldType.MonthYear => "month_year",
        FieldType.Timestamp => "timestamp",
        FieldType.MonthDay => "month_day",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// Parse a wire value. Unknown values fall back to <see cref="FieldType.Text"/>,
    /// matching the pre-enum <c>NormalizeType</c> behaviour in
    /// <c>CreateFieldCommandHandler</c>: server-persisted schemas frequently pre-date
    /// new type additions, and defaulting to plain text is the least-surprising story
    /// for downstream renderers.
    /// </summary>
    public static FieldType ParseWireValueOrText(string? value) => value switch
    {
        "text" => FieldType.Text,
        "number" => FieldType.Number,
        "boolean" => FieldType.Boolean,
        "date" => FieldType.Date,
        "colour" => FieldType.Colour,
        "plaintext" => FieldType.Plaintext,
        "month" => FieldType.Month,
        "year" => FieldType.Year,
        "month_year" => FieldType.MonthYear,
        "timestamp" => FieldType.Timestamp,
        "month_day" => FieldType.MonthDay,
        _ => FieldType.Text,
    };

    /// <summary>Persisted smallint code — the enum's declaration order matches the Scylla
    /// schema's 0–10 codes (mirroring <c>PollTypeExtensions</c>).</summary>
    public static short ToCode(this FieldType type) => (short)type;

    /// <summary>Maps the persisted smallint back; unknown codes fall back to
    /// <see cref="FieldType.Text"/> like <see cref="ParseWireValueOrText"/>.</summary>
    public static FieldType FromCode(short code)
        => code is >= 0 and <= (short)FieldType.MonthDay ? (FieldType)code : FieldType.Text;
}

/// <summary>
/// Custom converter because <see cref="FieldType.Plaintext"/>, <see cref="FieldType.MonthYear"/>
/// and <see cref="FieldType.MonthDay"/> do not match the default snake_case naming policy
/// output (which would emit <c>"plain_text"</c>, etc.) — we lock the wire values to what
/// the Elixir server historically emitted.
/// </summary>
internal sealed class FieldTypeJsonConverter : JsonConverter<FieldType>
{
    public override FieldType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => FieldTypeExtensions.ParseWireValueOrText(reader.GetString());

    public override void Write(Utf8JsonWriter writer, FieldType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToWireValue());
}
