using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Commands;

public sealed record RemoveFriendshipCommand(SystemId FriendSystemId);

public sealed record SetFriendTrustCommand(SystemId FriendSystemId, bool Trusted);

public sealed record SendFriendRequestCommand(SystemId TargetSystemId);

public sealed record AcceptFriendRequestCommand(SystemId SourceSystemId);

public sealed record RejectFriendRequestCommand(SystemId SourceSystemId);

public sealed record CancelFriendRequestCommand(SystemId TargetSystemId);
