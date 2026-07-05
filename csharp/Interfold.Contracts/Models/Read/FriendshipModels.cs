using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;

namespace Interfold.Contracts.Models.Read;

public sealed record FriendProfileReadModel(
    SystemId Id,
    string? Username,
    string? AvatarUrl,
    AvatarSource? AvatarSource,
    string? Description,
    string? DiscordId
);

// Fields carries the same per-alter custom-field entries as AlterReadModel.Fields; the Kotlin
// client reads it as List<ExternalAlterCustomField> (id/name/type/value). Today's only producer
// (ScyllaFriendshipRepository.GetFrontingAsync) sends an empty list.
public sealed record FriendFrontingAlterReadModel(
    AlterId Id,
    string? Name,
    string? Pronouns,
    string? Description,
    IReadOnlyList<AlterPublicFieldReadModel> Fields,
    string? AvatarUrl,
    AvatarSource? AvatarSource,
    IReadOnlyList<string> ExtraImages,
    string? Color
);

public sealed record FriendFrontingFrontReadModel(
    AlterId AlterId,
    string? Comment
);

public sealed record FriendFrontingReadModel(
    FriendFrontingAlterReadModel Alter,
    FriendFrontingFrontReadModel Front,
    bool Primary
);

public record FriendshipModel(
    FriendshipLevel Level,
    DateTimeOffset Since
);

public sealed record FriendshipReadModel(
    FriendProfileReadModel Friend,
    FriendshipModel Friendship,
    IReadOnlyList<FriendFrontingReadModel> Fronting
);

public sealed record FriendRequestReadModel(
    FriendProfileReadModel System,
    FriendshipRequestModel Request
);

public sealed record FriendshipRequestModel(
    DateTimeOffset DateSent
);


public sealed record FriendRequestIndexReadModel(
    IReadOnlyList<FriendRequestReadModel> Incoming,
    IReadOnlyList<FriendRequestReadModel> Outgoing
);

public enum SendFriendRequestOutcome
{
    Sent,
    Accepted,
    AlreadyFriends,
    AlreadySent,
    NoUser
}

public enum FriendRequestMutationOutcome
{
    Ok,
    AlreadyFriends,
    NotRequested,
    NoUser
}
