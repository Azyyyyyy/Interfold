using System.Text.Json.Serialization;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;

namespace Interfold.Friendships.Api.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(SuccessResponse<FriendshipReadModel>))]
[JsonSerializable(typeof(SuccessResponse<IReadOnlyList<FriendshipReadModel>>))]
[JsonSerializable(typeof(SuccessResponse<FriendRequestIndexReadModel>))]
public sealed partial class FriendshipsApiJsonContext : JsonSerializerContext;
