using System.Text.Json.Serialization;
using Interfold.Friendships.Contracts.Models.Commands;

namespace Interfold.Friendships.Contracts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RemoveFriendshipCommand))]
[JsonSerializable(typeof(SetFriendTrustCommand))]
[JsonSerializable(typeof(SendFriendRequestCommand))]
[JsonSerializable(typeof(AcceptFriendRequestCommand))]
[JsonSerializable(typeof(RejectFriendRequestCommand))]
[JsonSerializable(typeof(CancelFriendRequestCommand))]
[JsonSerializable(typeof(FriendshipCommandResult))]
public sealed partial class FriendshipsCommandJsonContext : JsonSerializerContext;
