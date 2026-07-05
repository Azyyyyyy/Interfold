using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// The client platform identifiers used by the firebase-config endpoint's
/// <c>?platform=</c> query value and the socket join payload's <c>platform</c> member.
/// Wire spellings are the lowercase names ("android" / "ios" / "web"); unknown values are
/// handled by callers via <see cref="EnumWireExtensions.TryParseClientPlatform"/> so the
/// legacy invalid-platform responses are preserved.
/// </summary>
[JsonConverter(typeof(LowerCaseEnumJsonConverter<ClientPlatform>))]
public enum ClientPlatform
{
    Android,
    Ios,
    Web,
}

/// <summary>
/// Tolerant converter for optional wire members (socket join payload): unknown or
/// non-string spellings read as null instead of throwing, mirroring the legacy handler
/// that only ever compared the raw string against known values. Writes the lowercase
/// wire spelling (or null).
/// </summary>
public sealed class TolerantClientPlatformJsonConverter : JsonConverter<ClientPlatform?>
{
    public override ClientPlatform? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? EnumWireExtensions.TryParseClientPlatform(reader.GetString())
            : null;

    public override void Write(Utf8JsonWriter writer, ClientPlatform? value, JsonSerializerOptions options)
    {
        if (value is { } platform)
        {
            writer.WriteStringValue(platform.ToWireValue());
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
