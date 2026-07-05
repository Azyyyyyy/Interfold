using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Read;

public sealed record AccountPublicProfileReadModel(
    SystemId SystemId,
    string? Username,
    string? Description,
    string? AvatarUrl,
    AvatarSource? AvatarSource,
    string? DiscordId,
    string? Email,
    string? AppleId
);

public enum AccountLinkResult
{
    Success,
    AlreadyLinked,
    UserExists,
    UserNotFound
}
