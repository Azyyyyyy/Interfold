using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Outcome verb carried on <c>FriendshipCommandResult.Action</c>. Serialized into persisted
/// idempotency outcome payloads and HTTP response bodies, so the snake-case-lower wire
/// spellings ("accepted", "sent", …) are frozen.
/// </summary>
[JsonConverter(typeof(LowerCaseEnumJsonConverter<FriendshipAction>))]
public enum FriendshipAction
{
    Accepted,
    Sent,
    Removed,
    Trusted,
    Untrusted,
    Rejected,
    Cancelled,
}
