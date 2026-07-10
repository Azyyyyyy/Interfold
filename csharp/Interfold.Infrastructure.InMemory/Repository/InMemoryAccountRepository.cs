using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryAccountRepository : IAccountRepository
{
    /// <summary>
    /// Slice 4 TTL — matches the 5-minute expiry the Scylla port enforces. Pre-Slice-4 the
    /// InMemory adapter had no expiry at all, so a link token issued at T+0 was still
    /// resolvable at T+1h, which diverged from the Scylla adapter and let integration
    /// tests silently accept stale tokens.
    /// </summary>
    private static readonly TimeSpan LinkTokenTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reverse map from link-token → (scoped systemId, expiry). Retyped in Slice 4 to
    /// carry a <see cref="ScopedSystemId"/> and an expiry timestamp so
    /// <see cref="ResolveSystemIdByLinkTokenAsync"/> can honour the same TTL contract as
    /// the Scylla port. The value tuple also lets us scrub stale entries lazily on the
    /// first read that sees them expired.
    /// </summary>
    private readonly record struct LinkTokenEntry(ScopedSystemId Scoped, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, string> _usernameBySystem = new();
    private readonly ConcurrentDictionary<string, string> _descriptionBySystem = new();
    private readonly ConcurrentDictionary<string, string> _avatarBySystem = new();
    private readonly ConcurrentDictionary<string, AvatarSource> _avatarSourceBySystem = new();
    private readonly ConcurrentDictionary<string, string> _linkTokenBySystem = new();
    private readonly ConcurrentDictionary<string, LinkTokenEntry> _systemByLinkToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _discordBySystem = new();
    private readonly ConcurrentDictionary<string, string> _emailBySystem = new();
    private readonly ConcurrentDictionary<string, string> _appleBySystem = new();
    private readonly ConcurrentDictionary<string, string> _systemByDiscord = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _systemByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _systemByApple = new(StringComparer.OrdinalIgnoreCase);

    private readonly IEncryptionStateRepository? _encryptionStates;
    private readonly IRegionContext _regionContext;
    // Slice 4 bug A: injectable clock so unit tests can exercise the TTL branch without a
    // 5-minute wall wait. Defaults to TimeProvider.System, so production wiring is
    // unchanged. Kept internal-shape (no interface indirection beyond TimeProvider) because
    // we only need "now" — no scheduled work runs on the repository.
    private readonly TimeProvider _timeProvider;

    public InMemoryAccountRepository(
        IRegionContext regionContext,
        IEncryptionStateRepository? encryptionStates = null,
        TimeProvider? timeProvider = null)
    {
        _regionContext = regionContext;
        _encryptionStates = encryptionStates;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<bool> UpdateUsernameAsync(SystemId systemId, Username username, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _usernameBySystem[systemKey] = username.Value;
        return Task.FromResult(true);
    }

    public Task<bool> UpdateDescriptionAsync(SystemId systemId, string description, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _descriptionBySystem[systemKey] = description;
        return Task.FromResult(true);
    }

    public Task<bool> UpdateAvatarAsync(SystemId systemId, AvatarUrl avatarUrl, AvatarSource source, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _avatarBySystem[systemKey] = avatarUrl.Value;
        _avatarSourceBySystem[systemKey] = source;
        return Task.FromResult(true);
    }

    public Task<bool> ClearAvatarAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _avatarBySystem.TryRemove(systemKey, out _);
        _avatarSourceBySystem.TryRemove(systemKey, out _);
        return Task.FromResult(true);
    }

    public Task<LinkToken> GetOrCreateLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var scoped = ResolveScoped(systemId);
        var now = _timeProvider.GetUtcNow();

        // Deterministic token derivation kept intact — the InMemory adapter's convention
        // is "same system → same token", which the integration tests lean on. Every call
        // refreshes the expiry so a live client that keeps calling get-or-create doesn't
        // spuriously expire.
        var token = _linkTokenBySystem.GetOrAdd(systemKey, static key =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hash)[..32].ToLowerInvariant();
        });

        _systemByLinkToken[token] = new LinkTokenEntry(scoped, now.Add(LinkTokenTtl));

        return Task.FromResult(new LinkToken(token));
    }

    public Task<LinkToken?> GetLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_linkTokenBySystem.TryGetValue(systemKey, out var token))
        {
            return Task.FromResult<LinkToken?>(null);
        }

        // Slice 4 bug A fix: honour the TTL on the read path so a client that only calls
        // "get" never sees a token that ResolveSystemIdByLinkTokenAsync would refuse.
        if (_systemByLinkToken.TryGetValue(token, out var entry) && entry.ExpiresAt > _timeProvider.GetUtcNow())
        {
            return Task.FromResult<LinkToken?>(new LinkToken(token));
        }

        ScrubLinkToken(systemKey, token);
        return Task.FromResult<LinkToken?>(null);
    }

    public Task<SystemId?> ResolveSystemIdByLinkTokenAsync(LinkToken linkToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(linkToken.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        // Slice 4 bug A + B fix: check TTL on lookup, and on miss (nonexistent or expired)
        // scrub BOTH sides so the deterministic-token semantics don't leave a dangling
        // pointer that a later GetOrCreate would silently re-adopt.
        if (_systemByLinkToken.TryGetValue(linkToken.Value, out var entry) && entry.ExpiresAt > _timeProvider.GetUtcNow())
        {
            return Task.FromResult<SystemId?>(entry.Scoped.AsSystemId());
        }

        ScrubLinkToken(linkTokenValue: linkToken.Value);
        return Task.FromResult<SystemId?>(null);
    }

    public Task<bool> ClearLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_linkTokenBySystem.TryRemove(systemKey, out var token))
        {
            _systemByLinkToken.TryRemove(token, out _);
        }

        return Task.FromResult(true);
    }

    public Task<SystemId?> TryFindSystemIdByDiscordIdAsync(DiscordId discordId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(discordId.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        return Task.FromResult<SystemId?>(
            _systemByDiscord.TryGetValue(discordId.Value, out var scopedSystemId)
                ? new SystemId(scopedSystemId)
                : null);
    }

    public Task<SystemId?> FindOrCreateSystemIdByDiscordIdAsync(DiscordId discordId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(discordId.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        if (_systemByDiscord.TryGetValue(discordId.Value, out var scopedSystemId))
        {
            return Task.FromResult<SystemId?>(new SystemId(scopedSystemId));
        }

        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNewSystemId = ScopedSystemId.Compose(_regionContext.ResolveUserRegion(new SystemId(newSystemId)), newSystemId).Value;
        _discordBySystem[newSystemId] = discordId.Value;
        _systemByDiscord[discordId.Value] = scopedNewSystemId;

        EnsureEncryptionSaltForSystem(scopedNewSystemId);
        return Task.FromResult<SystemId?>(new SystemId(scopedNewSystemId));
    }

    public Task<SystemId?> FindSystemIdByEmailAsync(Email email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        if (_systemByEmail.TryGetValue(email.Value, out var scopedSystemId))
        {
            return Task.FromResult<SystemId?>(new SystemId(scopedSystemId));
        }

        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNewSystemId = ScopedSystemId.Compose(_regionContext.ResolveUserRegion(new SystemId(newSystemId)), newSystemId).Value;
        _emailBySystem[newSystemId] = email.Value;
        _systemByEmail[email.Value] = scopedNewSystemId;

        EnsureEncryptionSaltForSystem(scopedNewSystemId);
        return Task.FromResult<SystemId?>(new SystemId(scopedNewSystemId));
    }

    public Task<SystemId?> FindSystemIdByAppleIdAsync(AppleId appleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appleId.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        if (_systemByApple.TryGetValue(appleId.Value, out var scopedSystemId))
        {
            return Task.FromResult<SystemId?>(new SystemId(scopedSystemId));
        }

        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNewSystemId = ScopedSystemId.Compose(_regionContext.ResolveUserRegion(new SystemId(newSystemId)), newSystemId).Value;
        _appleBySystem[newSystemId] = appleId.Value;
        _systemByApple[appleId.Value] = scopedNewSystemId;

        EnsureEncryptionSaltForSystem(scopedNewSystemId);
        return Task.FromResult<SystemId?>(new SystemId(scopedNewSystemId));
    }

    public Task<AccountLinkResult> LinkDiscordToUserAsync(SystemId systemId, DiscordId discordId, CancellationToken cancellationToken = default)
        => Task.FromResult(LinkIdentifier(systemId, discordId.Value, _discordBySystem, _systemByDiscord));

    public Task<AccountLinkResult> LinkEmailToUserAsync(SystemId systemId, Email email, CancellationToken cancellationToken = default)
        => Task.FromResult(LinkIdentifier(systemId, email.Value, _emailBySystem, _systemByEmail));

    public Task<AccountLinkResult> LinkAppleToUserAsync(SystemId systemId, AppleId appleId, CancellationToken cancellationToken = default)
        => Task.FromResult(LinkIdentifier(systemId, appleId.Value, _appleBySystem, _systemByApple));

    public Task<bool> UnlinkDiscordAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_discordBySystem.TryRemove(systemKey, out var discordId) && !string.IsNullOrWhiteSpace(discordId))
        {
            _systemByDiscord.TryRemove(discordId, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> UnlinkEmailAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_emailBySystem.TryRemove(systemKey, out var email) && !string.IsNullOrWhiteSpace(email))
        {
            _systemByEmail.TryRemove(email, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> UnlinkAppleAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_appleBySystem.TryRemove(systemKey, out var appleId) && !string.IsNullOrWhiteSpace(appleId))
        {
            _systemByApple.TryRemove(appleId, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        _usernameBySystem.TryRemove(systemKey, out _);
        _descriptionBySystem.TryRemove(systemKey, out _);
        _avatarBySystem.TryRemove(systemKey, out _);
        _avatarSourceBySystem.TryRemove(systemKey, out _);
        if (_linkTokenBySystem.TryRemove(systemKey, out var token))
        {
            _systemByLinkToken.TryRemove(token, out _);
        }

        if (_discordBySystem.TryRemove(systemKey, out var discordId) && !string.IsNullOrWhiteSpace(discordId))
        {
            _systemByDiscord.TryRemove(discordId, out _);
        }

        if (_emailBySystem.TryRemove(systemKey, out var email) && !string.IsNullOrWhiteSpace(email))
        {
            _systemByEmail.TryRemove(email, out _);
        }

        if (_appleBySystem.TryRemove(systemKey, out var appleId) && !string.IsNullOrWhiteSpace(appleId))
        {
            _systemByApple.TryRemove(appleId, out _);
        }

        return Task.FromResult(true);
    }

    public Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var username = _usernameBySystem.TryGetValue(systemKey, out var u) ? u : null;
        var description = _descriptionBySystem.TryGetValue(systemKey, out var d) ? d : null;
        var avatarUrl = _avatarBySystem.TryGetValue(systemKey, out var a) ? a : null;
        AvatarSource? avatarSource = _avatarSourceBySystem.TryGetValue(systemKey, out var s) ? s : null;
        var discordId = _discordBySystem.TryGetValue(systemKey, out var discord) ? discord : null;
        var email = _emailBySystem.TryGetValue(systemKey, out var e) ? e : null;
        var appleId = _appleBySystem.TryGetValue(systemKey, out var apple) ? apple : null;

        if (username is null && description is null && avatarUrl is null && discordId is null && email is null && appleId is null)
        {
            return Task.FromResult<AccountPublicProfileReadModel?>(null);
        }

        return Task.FromResult<AccountPublicProfileReadModel?>(
            new AccountPublicProfileReadModel(
                systemId,
                username is null ? null : new Username(username),
                description,
                AvatarUrl.FromNullable(avatarUrl),
                avatarSource,
                discordId is null ? null : new DiscordId(discordId),
                email is null ? null : new Email(email),
                appleId is null ? null : new AppleId(appleId)));
    }

    private string GetSystemKey(SystemId systemId) => InMemoryStorageKeys.ForSystem(_regionContext, systemId);

    private ScopedSystemId ResolveScoped(SystemId systemId)
        => ScopedSystemId.Compose(_regionContext.ResolveUserRegion(systemId), systemId);

    /// <summary>
    /// Drop a link-token from both maps. Used by the two read paths that discover a stale
    /// or missing entry — the deterministic-token hash means we cannot rely on a
    /// re-issued token to bury the stale mapping, so lazy scrub on read is the guardrail
    /// against dangling reverse-map pointers (Slice 4 bug B).
    /// </summary>
    private void ScrubLinkToken(string? systemKey = null, string? linkTokenValue = null)
    {
        if (linkTokenValue is not null)
        {
            _systemByLinkToken.TryRemove(linkTokenValue, out _);
            // Also drop the systemKey → token pointer if it still references this token
            // (deterministic-hash tokens make this cheap; no scan of the entire dictionary).
            foreach (var kvp in _linkTokenBySystem)
            {
                if (string.Equals(kvp.Value, linkTokenValue, StringComparison.Ordinal))
                {
                    _linkTokenBySystem.TryRemove(kvp.Key, out _);
                    break;
                }
            }
        }

        if (systemKey is not null && _linkTokenBySystem.TryRemove(systemKey, out var token))
        {
            _systemByLinkToken.TryRemove(token, out _);
        }
    }

    private AccountLinkResult LinkIdentifier(
        SystemId systemId,
        string identifier,
        ConcurrentDictionary<string, string> identifierBySystem,
        ConcurrentDictionary<string, string> systemByIdentifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return AccountLinkResult.UserNotFound;
        }

        var systemKey = GetSystemKey(systemId);
        var scopedSystemId = ResolveScoped(systemId).Value;

        if (_usernameBySystem.ContainsKey(systemKey) is false &&
            _descriptionBySystem.ContainsKey(systemKey) is false &&
            _avatarBySystem.ContainsKey(systemKey) is false &&
            _linkTokenBySystem.ContainsKey(systemKey) is false)
        {
            return AccountLinkResult.UserNotFound;
        }

        if (identifierBySystem.TryGetValue(systemKey, out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            return AccountLinkResult.AlreadyLinked;
        }

        if (systemByIdentifier.TryGetValue(identifier, out var owner) && !string.Equals(owner, scopedSystemId, StringComparison.Ordinal))
        {
            return AccountLinkResult.UserExists;
        }

        identifierBySystem[systemKey] = identifier;
        systemByIdentifier[identifier] = scopedSystemId;
        return AccountLinkResult.Success;
    }

    private void EnsureEncryptionSaltForSystem(string scopedSystemId)
    {
        if (_encryptionStates is null)
            return;

        var saltBytes = RandomNumberGenerator.GetBytes(32);
        var salt = Convert.ToBase64String(saltBytes);
        _ = _encryptionStates.UpsertAsync(new SystemId(scopedSystemId), false, null, new EncryptionSalt(salt), CancellationToken.None);
    }
}
