using System.Text.Json.Serialization;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Shared.Contracts;
using Interfold.Socket.Contracts;

namespace Interfold.Friendships.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(FriendshipReadModel))]
[JsonSerializable(typeof(IReadOnlyList<FriendshipReadModel>))]
[JsonSerializable(typeof(FriendRequestIndexReadModel))]
[JsonSerializable(typeof(FriendRequestReadModel))]
[JsonSerializable(typeof(FriendshipRequestModel))]
[JsonSerializable(typeof(FriendProfileReadModel))]
[JsonSerializable(typeof(FriendFrontingReadModel))]
[JsonSerializable(typeof(FriendRequestSocketPayload))]
[JsonSerializable(typeof(FriendIdSocketPayload))]
[JsonSerializable(typeof(PhxFrame<FriendRequestSocketPayload>))]
[JsonSerializable(typeof(PhxFrame<FriendIdSocketPayload>))]
public sealed partial class FriendshipsContractsJsonContext : JsonSerializerContext;
