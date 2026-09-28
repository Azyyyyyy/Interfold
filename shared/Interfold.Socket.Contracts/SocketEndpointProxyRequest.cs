using System.Text.Json.Serialization;

namespace Interfold.Socket.Contracts;

/// <summary>
/// Typed shape of the <c>endpoint</c> Phoenix frame payload. <c>Body</c> is a raw
/// JSON string (Phoenix wraps the inner API request body as a JSON-string field so
/// it can be forwarded to the loopback API without being re-serialized — see the
/// forwarding comment in <c>WebSocketHandler.HandleEndpointProxyAsync</c>).
/// Optional W3C <c>traceparent</c>/<c>tracestate</c> parent the proxied REST under
/// the client's <c>sendAPIRequest</c> span when present.
/// </summary>
public sealed record SocketEndpointProxyRequest(
    string? Method,
    string? Path,
    string? Body,
    [property: JsonPropertyName("traceparent")] string? Traceparent = null,
    [property: JsonPropertyName("tracestate")] string? Tracestate = null);
