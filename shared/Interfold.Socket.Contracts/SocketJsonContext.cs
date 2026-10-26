using System.Text.Json.Serialization;
using Interfold.Shared.Contracts;

namespace Interfold.Socket.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(PhxJoinPayload))]
[JsonSerializable(typeof(PhxEndpointPayload))]
[JsonSerializable(typeof(PhxEndpointPayload<object>))]
[JsonSerializable(typeof(PhxFrame<PhxJoinPayload>))]
[JsonSerializable(typeof(PhxFrame<PhxEndpointPayload>))]
[JsonSerializable(typeof(PhxFrame<PhxEndpointPayload<object>>))]
[JsonSerializable(typeof(SocketEndpointProxyRequest))]
[JsonSerializable(typeof(SocketEndpointProxyResponse))]
[JsonSerializable(typeof(EmptyPayload))]
[JsonSerializable(typeof(SocketReasonResponse))]
[JsonSerializable(typeof(SocketSelfReadModel))]
[JsonSerializable(typeof(SocketJoinReconnectPayload))]
[JsonSerializable(typeof(SystemIdSocketPayload))]
[JsonSerializable(typeof(PhoenixReplyPayload<EmptyPayload>))]
[JsonSerializable(typeof(PhoenixReplyPayload<SocketReasonResponse>))]
[JsonSerializable(typeof(PhoenixReplyPayload<SocketEndpointProxyResponse>))]
[JsonSerializable(typeof(PhoenixReplyPayload<SocketSelfReadModel>))]
[JsonSerializable(typeof(PhxFrame<EmptyPayload>))]
[JsonSerializable(typeof(PhxFrame<SocketReasonResponse>))]
[JsonSerializable(typeof(PhxFrame<SocketEndpointProxyResponse>))]
[JsonSerializable(typeof(PhxFrame<SocketSelfReadModel>))]
[JsonSerializable(typeof(PhxFrame<SocketJoinReconnectPayload>))]
[JsonSerializable(typeof(PhxFrame<SystemIdSocketPayload>))]
[JsonSerializable(typeof(PhxFrame<PhoenixReplyPayload<EmptyPayload>>))]
[JsonSerializable(typeof(PhxFrame<PhoenixReplyPayload<SocketReasonResponse>>))]
[JsonSerializable(typeof(PhxFrame<PhoenixReplyPayload<SocketEndpointProxyResponse>>))]
public sealed partial class SocketJsonContext : JsonSerializerContext;
