using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Type of a user-defined settings field. The wire representation is the lowercase
/// snake_case name (<c>"text"</c>, <c>"month_year"</c>, <c>"month_day"</c>, ...),
/// preserving what the Elixir server emitted so the Kotlin client and any prior
/// captured payloads deserialize unchanged.
/// </summary>
/// <remarks>
/// JSON deserialization is strict: unknown wire values throw. The tolerant
/// <see cref="FieldTypeExtensions.ParseWireValueOrText"/> stays for the non-JSON
/// CQL row-mapping path, where server-persisted schemas may pre-date new enum values.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<FieldType>))]
public enum FieldType : short
{
    [JsonStringEnumMemberName("text")]
    Text = 0,

    [JsonStringEnumMemberName("number")]
    Number = 1,

    [JsonStringEnumMemberName("boolean")]
    Boolean = 2,

    [JsonStringEnumMemberName("date")]
    Date = 3,

    [JsonStringEnumMemberName("colour")]
    Colour = 4,

    [JsonStringEnumMemberName("plaintext")]
    Plaintext = 5,

    [JsonStringEnumMemberName("month")]
    Month = 6,

    [JsonStringEnumMemberName("year")]
    Year = 7,

    [JsonStringEnumMemberName("month_year")]
    MonthYear = 8,

    [JsonStringEnumMemberName("timestamp")]
    Timestamp = 9,

    [JsonStringEnumMemberName("month_day")]
    MonthDay = 10,
}

public static class FieldTypeExtensions
{
    /// <summary>
    /// Case-insensitive parse of a wire value. Unknown values fall back to
    /// <see cref="FieldType.Text"/>, matching the pre-enum <c>NormalizeType</c> behaviour in
    /// <c>CreateFieldCommandHandler</c>: server-persisted schemas frequently pre-date new type
    /// additions, and defaulting to plain text is the least-surprising story for downstream
    /// renderers.
    /// </summary>
    public static FieldType ParseWireValueOrText(string? value)
        => value.TryParseWire<FieldType>(out var type) ? type : FieldType.Text;
}
