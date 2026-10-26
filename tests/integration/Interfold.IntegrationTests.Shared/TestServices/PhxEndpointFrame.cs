using System.Text.Json;

namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>
/// Test-side builder for the Phoenix <c>endpoint</c> frame used by the WebSocket integration
/// suite. The body argument is already-canonical JSON (no STJ on the body).
/// </summary>
public static class PhxEndpointFrame
{
    public const string EmptyBody = "{}";

    /// <summary>
    /// Build the serialised bytes of an <c>endpoint</c> frame targeting
    /// <paramref name="topic"/>. Defaults to <c>JoinRef = "1"</c> because every
    /// existing call site's join was request-ref <c>"1"</c>; callers with a
    /// different join sequence pass the correct value explicitly.
    /// </summary>
    public static byte[] Build(
        string topic,
        string method,
        string path,
        string bodyJson,
        string @ref,
        string? joinRef = "1")
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("topic", topic);
            writer.WriteString("event", "endpoint");
            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            writer.WriteString("method", method);
            writer.WriteString("path", path);
            writer.WriteString("body", bodyJson);
            writer.WriteEndObject();
            writer.WriteString("ref", @ref);
            if (joinRef is null)
                writer.WriteNull("join_ref");
            else
                writer.WriteString("join_ref", joinRef);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
