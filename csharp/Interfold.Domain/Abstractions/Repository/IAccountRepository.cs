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

    /// <summary>
    /// Read-only lookup: returns the system id linked to <paramref name="discordId"/>, or
    /// <see langword="null"/> if none exists. Does NOT create an account on miss — use this
    /// from surfaces where an unknown Discord id should surface as "no such user" (e.g. the
    /// friendship <c>Kind.Discord</c> resolve branch) rather than spawn a phantom account.
    /// </summary>
    /// <remarks>
    /// This is the only surviving identity-specific find method post-Round-5. Its Email /
    /// Apple siblings never had a caller — the OAuth-login flow always goes through
    /// <see cref="FindOrCreateSystemIdAsync"/> which auto-provisions on miss, and the
    /// friendship resolve branch only supports the Discord shape. Kept as a distinct
    /// method (rather than adding an "auto-provision?" flag to the consolidated
    /// <see cref="FindOrCreateSystemIdAsync"/>) so the intent is legible at the call
    /// site and the InMemory repo can dispatch to a truly read-only branch that never
    /// touches the encryption-salt bootstrap path.
    /// </remarks>
    Task<SystemId?> TryFindSystemIdByDiscordIdAsync(DiscordId discordId, CancellationToken cancellationToken = default);

    /// <summary>
    /// OAuth-login shape: returns the system id linked to the OAuth
    /// <paramref name="identity"/>, or auto-provisions a new account for it (with a
    /// fresh encryption salt) if none exists. Callers that must not auto-provision on
    /// miss (e.g. the friendship resolver's Discord branch) should call
    /// <see cref="TryFindSystemIdByDiscordIdAsync"/> instead.
    ///
    /// <para>
    /// Post-Round-5 sanity check (Finding 6): consolidates the pre-Round-5 fan-out of
    /// <c>FindOrCreateSystemIdByDiscordIdAsync</c> / <c>FindSystemIdByEmailAsync</c> /
    /// <c>FindSystemIdByAppleIdAsync</c> onto the <see cref="ProviderIdentity"/>
    /// discriminated union that Round-2 Commit 7 introduced. The prior spelling had two
    /// coupled footguns: the fan-out mirrored the same 3-branch pattern match in every
    /// caller (AuthController.Callback + AuthLinkController.Callback), and the "Find"
    /// prefix on the Email / Apple methods silently hid the auto-provisioning semantics
    /// — only the Discord method's name (with its explicit "FindOrCreate") reflected
    /// the create-on-miss behaviour truthfully. The consolidated name honours the
    /// invariant for all three providers and both callers now collapse to a single
    /// non-dispatching call.
    /// </para>
    ///
    /// <para>
    /// Returns <see langword="null"/> when <paramref name="identity"/> carries no
    /// populated variant. Every construction site funnels through one of the
    /// <see cref="ProviderIdentity.FromDiscord(DiscordId)"/> /
    /// <see cref="ProviderIdentity.FromGoogle(Email)"/> /
    /// <see cref="ProviderIdentity.FromApple(AppleId)"/> factories so this branch is
    /// unreachable in practice — the guard exists to make an accidental
    /// <c>default(ProviderIdentity)</c> pass surface as "no user" rather than as a
    /// dispatch panic.
    /// </para>
    /// </summary>
    Task<SystemId?> FindOrCreateSystemIdAsync(ProviderIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Link the OAuth <paramref name="identity"/> to the existing user identified by
    /// <paramref name="systemId"/>. Returns <see cref="AccountLinkResult.Success"/>,
    /// <see cref="AccountLinkResult.UserNotFound"/>, or
    /// <see cref="AccountLinkResult.AlreadyLinked"/> per the underlying repository's
    /// contract.
    ///
    /// <para>
    /// Post-Round-5 sanity check (Finding 6): consolidates the pre-Round-5 fan-out of
    /// <c>LinkDiscordToUserAsync</c> / <c>LinkEmailToUserAsync</c> /
    /// <c>LinkAppleToUserAsync</c> onto <see cref="ProviderIdentity"/>. See
    /// <see cref="FindOrCreateSystemIdAsync"/> for the same rationale.
    /// </para>
    /// </summary>
    Task<AccountLinkResult> LinkIdentityToUserAsync(SystemId systemId, ProviderIdentity identity, CancellationToken cancellationToken = default);

    Task<bool> UnlinkDiscordAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<bool> UnlinkEmailAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<bool> UnlinkAppleAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(SystemId systemId, CancellationToken cancellationToken = default);
}
