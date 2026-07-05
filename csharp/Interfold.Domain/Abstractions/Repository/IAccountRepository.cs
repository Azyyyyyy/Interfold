using Interfold.Contracts.Enums;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions.Repository;

public interface IAccountRepository
{
    Task<bool> UpdateUsernameAsync(SystemId systemId, Username username, CancellationToken cancellationToken = default);

    Task<bool> UpdateDescriptionAsync(SystemId systemId, string description, CancellationToken cancellationToken = default);

    Task<bool> UpdateAvatarAsync(SystemId systemId, AvatarUrl avatarUrl, AvatarSource source, CancellationToken cancellationToken = default);

    Task<bool> ClearAvatarAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<LinkToken> GetOrCreateLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only lookup: returns the existing link token for <paramref name="systemId"/>,
    /// or <see langword="null"/> if none has been created yet.
    /// Safe to call on non-primary nodes.
    /// </summary>
    Task<LinkToken?> GetLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<SystemId?> ResolveSystemIdByLinkTokenAsync(LinkToken linkToken, CancellationToken cancellationToken = default);

    Task<bool> ClearLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<SystemId?> FindSystemIdByDiscordIdAsync(DiscordId discordId, CancellationToken cancellationToken = default);

    Task<SystemId?> FindSystemIdByEmailAsync(Email email, CancellationToken cancellationToken = default);

    Task<SystemId?> FindSystemIdByAppleIdAsync(AppleId appleId, CancellationToken cancellationToken = default);

    Task<AccountLinkResult> LinkDiscordToUserAsync(SystemId systemId, DiscordId discordId, CancellationToken cancellationToken = default);

    Task<AccountLinkResult> LinkEmailToUserAsync(SystemId systemId, Email email, CancellationToken cancellationToken = default);

    Task<AccountLinkResult> LinkAppleToUserAsync(SystemId systemId, AppleId appleId, CancellationToken cancellationToken = default);

    Task<bool> UnlinkDiscordAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<bool> UnlinkEmailAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<bool> UnlinkAppleAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(SystemId systemId, CancellationToken cancellationToken = default);
}
