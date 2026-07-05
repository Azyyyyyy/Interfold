using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// An avatar (or extra-image) URL. Carries either a server-relative path (when the paired
/// <c>AvatarSource</c> is <c>Local</c> — qualified to an absolute URL at the API boundary by
/// <c>AvatarUrlQualifier</c>) or an absolute external URL. Construction does not validate:
/// legacy rows and passthrough imports carry arbitrary spellings. JSON serializes as the raw
/// string, so read models, socket frames, and hashed command payloads are unchanged.
/// </summary>
[JsonConverter(typeof(AvatarUrlJsonConverter))]
public readonly record struct AvatarUrl
{
    public string Value { get; }

    public AvatarUrl(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override string ToString() => Value;

    /// <summary>Null-preserving wrap for DB/state reads.</summary>
    public static AvatarUrl? FromNullable(string? value) => value is null ? null : new AvatarUrl(value);
}

internal sealed class AvatarUrlJsonConverter : JsonConverter<AvatarUrl>
{
    public override AvatarUrl Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, AvatarUrl value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
