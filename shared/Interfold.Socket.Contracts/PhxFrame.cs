using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Socket.Contracts;

/// <summary>
/// A Phoenix-protocol WebSocket frame (object format).
/// </summary>
public sealed class PhxFrame<TPayload>
{
    [JsonPropertyName("topic")]
    public string Topic { get; init; } = string.Empty;

    [JsonPropertyName("event")]
    public string Event { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public TPayload? Payload { get; init; }

    [JsonPropertyName("ref")]
    public string? Ref { get; init; }

    [JsonPropertyName("join_ref")]
    public string? JoinRef { get; init; }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("topic", Topic);
            writer.WriteString("event", Event);
            writer.WritePropertyName("payload");
            if (Payload is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<TPayload>)
                    SocketJson.Options.GetTypeInfo(typeof(TPayload));
                JsonSerializer.Serialize(writer, Payload, typeInfo);
            }

            WriteNullableString(writer, "ref", Ref);
            WriteNullableString(writer, "join_ref", JoinRef);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }
}

/// <summary>
/// Payload for <c>phx_join</c> frames.
/// </summary>
public sealed class PhxJoinPayload
{
    [JsonPropertyName("token")]
    public Shared.Contracts.Ids.SocketToken Token { get; init; } = new(string.Empty);

    [JsonPropertyName("protocolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtocolVersion { get; init; }

    // Join-side platform tolerates unknown wire spellings → null (so a stray value can't
    // fail the whole join). Every other ClientPlatform site keeps the strict converter.
    [JsonPropertyName("platform")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(TolerantWireEnumJsonConverter<ClientPlatform>))]
    public ClientPlatform? Platform { get; init; }

    [JsonPropertyName("isReconnect")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsReconnect { get; init; }

    [JsonPropertyName("forceBatch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ForceBatch { get; init; }
}

public sealed class PhxEndpointPayload : PhxEndpointPayload<object>;

/// <summary>
/// Payload for <c>endpoint</c> frames (proxied HTTP calls over WebSocket).
/// </summary>
public class PhxEndpointPayload<T> where T : new()
{
    [JsonPropertyName("method")]
    public string Method { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("body"), JsonConverter(typeof(BodyJson<object>))]
    public T Body { get; init; } = new T();
}

public class BodyJson<T> : JsonConverter<T>
    where T : new()
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new Exception("Expected string token for JSON body");
        }

        var rawContent = reader.GetString();
        if (string.IsNullOrWhiteSpace(rawContent))
            return new T();

        var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize(rawContent, typeInfo);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteStringValue("");
            return;
        }

        var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        writer.WriteStringValue(JsonSerializer.Serialize(value, typeInfo));
    }
}

/// <summary>
/// Helper for constructing Phoenix array-format WebSocket frames
/// (used when testing the array-protocol variant).
/// </summary>
public static class PhxArrayFrame
{
    public static byte[] CreateBytes<TPayload>(
        string? joinRef, string? @ref, string topic, string @event, TPayload payload,
        JsonSerializerOptions? options = null)
    {
        options ??= SocketJson.Options;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            WriteStringOrNull(writer, joinRef);
            WriteStringOrNull(writer, @ref);
            writer.WriteStringValue(topic);
            writer.WriteStringValue(@event);
            var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<TPayload>)
                options.GetTypeInfo(typeof(TPayload));
            JsonSerializer.Serialize(writer, payload, typeInfo);
            writer.WriteEndArray();
        }

        return stream.ToArray();
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string? value)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
