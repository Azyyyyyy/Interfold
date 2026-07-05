using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Commands;

public sealed record RemoveFriendshipCommand(SystemId FriendSystemId);

public sealed record SetFriendTrustCommand(SystemId FriendSystemId, bool Trusted);

// TargetSystemId's name is frozen (persisted payload + idempotency hashes); the type is
// UsernameOrSystemId because the route accepts usernames as well as system ids.
public sealed record SendFriendRequestCommand(UsernameOrSystemId TargetSystemId);

public sealed record AcceptFriendRequestCommand(SystemId SourceSystemId);

public sealed record RejectFriendRequestCommand(SystemId SourceSystemId);

public sealed record CancelFriendRequestCommand(SystemId TargetSystemId);
