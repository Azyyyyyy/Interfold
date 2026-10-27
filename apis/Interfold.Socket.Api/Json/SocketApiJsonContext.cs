using System.Text.Json.Serialization;
using Interfold.Shared.Contracts;
using Interfold.Socket.Api.Models;
using Interfold.Socket.Contracts;

namespace Interfold.Socket.Api.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(SocketJoinInitPayload))]
[JsonSerializable(typeof(SocketJoinBatchedPayload))]
[JsonSerializable(typeof(SocketBatchedAltersPayload))]
[JsonSerializable(typeof(PhoenixReplyPayload<SocketJoinInitPayload>))]
[JsonSerializable(typeof(PhoenixReplyPayload<SocketJoinBatchedPayload>))]
[JsonSerializable(typeof(PhxFrame<SocketJoinInitPayload>))]
[JsonSerializable(typeof(PhxFrame<SocketJoinBatchedPayload>))]
[JsonSerializable(typeof(PhxFrame<SocketBatchedAltersPayload>))]
[JsonSerializable(typeof(PhxFrame<PhoenixReplyPayload<SocketJoinInitPayload>>))]
[JsonSerializable(typeof(PhxFrame<PhoenixReplyPayload<SocketJoinBatchedPayload>>))]
public sealed partial class SocketApiJsonContext : JsonSerializerContext;
