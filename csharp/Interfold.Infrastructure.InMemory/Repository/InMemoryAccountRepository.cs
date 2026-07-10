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

    // Round-2 Commit 12 (canvas #22): typed dictionary state inside the InMemory account
    // repo. Keys stay as string-typed system-key (from GetSystemKey via
    // InMemoryStorageKeys.ForSystem) because retyping GetSystemKey to return
    // ScopedSystemId is a cross-cutting change to six repositories and is deferred to a
    // follow-up. Value fields promote to their canonical wrappers (Username, AvatarUrl,
    // DiscordId, Email, AppleId, LinkToken, ScopedSystemId) so the stored state carries
    // the same type discipline as the interface boundaries. The _systemBy{Discord,Email,
    // Apple} dicts retain their StringComparer.OrdinalIgnoreCase KEY comparer — the
    // identity types don't self-normalise, so the lookup contract is unchanged.
    // Descriptions stay string-typed because there is no Description wrapper (they are
    // genuinely free-form user content, not identifiers).
    private readonly ConcurrentDictionary<string, Username> _usernameBySystem = new();
    private readonly ConcurrentDictionary<string, string> _descriptionBySystem = new();
    private readonly ConcurrentDictionary<string, AvatarUrl> _avatarBySystem = new();
    private readonly ConcurrentDictionary<string, AvatarSource> _avatarSourceBySystem = new();
    private readonly ConcurrentDictionary<string, LinkToken> _linkTokenBySystem = new();
    private readonly ConcurrentDictionary<LinkToken, LinkTokenEntry> _systemByLinkToken = new();
    private readonly ConcurrentDictionary<string, DiscordId> _discordBySystem = new();
    private readonly ConcurrentDictionary<string, Email> _emailBySystem = new();
    private readonly ConcurrentDictionary<string, AppleId> _appleBySystem = new();
    private readonly ConcurrentDictionary<string, ScopedSystemId> _systemByDiscord = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ScopedSystemId> _systemByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ScopedSystemId> _systemByApple = new(StringComparer.OrdinalIgnoreCase);

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
        _usernameBySystem[systemKey] = username;
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
        _avatarBySystem[systemKey] = avatarUrl;
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
        // spuriously expire. Round-2 Commit 12: the derived hash is wrapped as LinkToken
        // immediately in the GetOrAdd factory so the token spends zero time as a bare
        // string local — every downstream reference goes through the redacting wrapper.
        var token = _linkTokenBySystem.GetOrAdd(systemKey, static key =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return new LinkToken(Convert.ToHexString(hash)[..32].ToLowerInvariant());
        });

        _systemByLinkToken[token] = new LinkTokenEntry(scoped, now.Add(LinkTokenTtl));

        return Task.FromResult(token);
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
            return Task.FromResult<LinkToken?>(token);
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
        if (_systemByLinkToken.TryGetValue(linkToken, out var entry) && entry.ExpiresAt > _timeProvider.GetUtcNow())
        {
            return Task.FromResult<SystemId?>(entry.Scoped.AsSystemId());
        }

        ScrubLinkToken(linkTokenValue: linkToken);
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

        // Round-2 Commit 12: _systemByDiscord now stores ScopedSystemId — pattern-match to
        // widen through AsSystemId in one hop.
        return Task.FromResult<SystemId?>(
            _systemByDiscord.TryGetValue(discordId.Value, out var scopedSystemId)
                ? scopedSystemId.AsSystemId()
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
            return Task.FromResult<SystemId?>(scopedSystemId.AsSystemId());
        }

        // Round-2 Commit 9: hold the ScopedSystemId typed locally so EnsureEncryptionSaltForSystem
        // (which now takes ScopedSystemId per canvas #21) receives the wrapper directly, and the
        // return can widen through AsSystemId without the pre-Round-2 raw `.Value` → `new SystemId(...)`
        // round-trip. Round-2 Commit 12: identity-value dicts now speak the typed DiscordId
        // wrapper; reverse-map dicts store ScopedSystemId — the assignment lines lose all the
        // `.Value` unwraps that used to poke the string-typed slots.
        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNew = ScopedSystemId.Compose(_regionContext.ResolveUserRegion(new SystemId(newSystemId)), newSystemId);
        _discordBySystem[newSystemId] = discordId;
        _systemByDiscord[discordId.Value] = scopedNew;

        EnsureEncryptionSaltForSystem(scopedNew);
        return Task.FromResult<SystemId?>(scopedNew.AsSystemId());
    }

    public Task<SystemId?> FindSystemIdByEmailAsync(Email email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        if (_systemByEmail.TryGetValue(email.Value, out var scopedSystemId))
        {
            return Task.FromResult<SystemId?>(scopedSystemId.AsSystemId());
        }

        // Round-2 Commit 9 + 12: see FindOrCreateSystemIdByDiscordIdAsync above for rationale.
        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNew = ScopedSystemId.Compose(_regionContext.ResolveUserRegion(new SystemId(newSystemId)), newSystemId);
        _emailBySystem[newSystemId] = email;
        _systemByEmail[email.Value] = scopedNew;

        EnsureEncryptionSaltForSystem(scopedNew);
        return Task.FromResult<SystemId?>(scopedNew.AsSystemId());
    }

    public Task<SystemId?> FindSystemIdByAppleIdAsync(AppleId appleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appleId.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        if (_systemByApple.TryGetValue(appleId.Value, out var scopedSystemId))
        {
            return Task.FromResult<SystemId?>(scopedSystemId.AsSystemId());
        }

        // Round-2 Commit 9 + 12: see FindOrCreateSystemIdByDiscordIdAsync above for rationale.
        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNew = ScopedSystemId.Compose(_regionContext.ResolveUserRegion(new SystemId(newSystemId)), newSystemId);
        _appleBySystem[newSystemId] = appleId;
        _systemByApple[appleId.Value] = scopedNew;

        EnsureEncryptionSaltForSystem(scopedNew);
        return Task.FromResult<SystemId?>(scopedNew.AsSystemId());
    }

    public Task<AccountLinkResult> LinkDiscordToUserAsync(SystemId systemId, DiscordId discordId, CancellationToken cancellationToken = default)
        => Task.FromResult(LinkIdentifier(systemId, discordId, _discordBySystem, _systemByDiscord, static id => id.Value));

    public Task<AccountLinkResult> LinkEmailToUserAsync(SystemId systemId, Email email, CancellationToken cancellationToken = default)
        => Task.FromResult(LinkIdentifier(systemId, email, _emailBySystem, _systemByEmail, static e => e.Value));

    public Task<AccountLinkResult> LinkAppleToUserAsync(SystemId systemId, AppleId appleId, CancellationToken cancellationToken = default)
        => Task.FromResult(LinkIdentifier(systemId, appleId, _appleBySystem, _systemByApple, static id => id.Value));

    public Task<bool> UnlinkDiscordAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_discordBySystem.TryRemove(systemKey, out var discordId) && !string.IsNullOrWhiteSpace(discordId.Value))
        {
            _systemByDiscord.TryRemove(discordId.Value, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> UnlinkEmailAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_emailBySystem.TryRemove(systemKey, out var email) && !string.IsNullOrWhiteSpace(email.Value))
        {
            _systemByEmail.TryRemove(email.Value, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> UnlinkAppleAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_appleBySystem.TryRemove(systemKey, out var appleId) && !string.IsNullOrWhiteSpace(appleId.Value))
        {
            _systemByApple.TryRemove(appleId.Value, out _);
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

        if (_discordBySystem.TryRemove(systemKey, out var discordId) && !string.IsNullOrWhiteSpace(discordId.Value))
        {
            _systemByDiscord.TryRemove(discordId.Value, out _);
        }

        if (_emailBySystem.TryRemove(systemKey, out var email) && !string.IsNullOrWhiteSpace(email.Value))
        {
            _systemByEmail.TryRemove(email.Value, out _);
        }

        if (_appleBySystem.TryRemove(systemKey, out var appleId) && !string.IsNullOrWhiteSpace(appleId.Value))
        {
            _systemByApple.TryRemove(appleId.Value, out _);
        }

        return Task.FromResult(true);
    }

    public Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        // Round-2 Commit 12: every dict now stores the typed wrapper directly, so the
        // read-side conditional promotes the value-type to its nullable form and the
        // final AccountPublicProfileReadModel constructor accepts the wrappers without
        // a `new Username(u)` / `new DiscordId(discord)` re-wrap. Descriptions stay
        // string-typed (no wrapper).
        Username? username = _usernameBySystem.TryGetValue(systemKey, out var u) ? u : null;
        var description = _descriptionBySystem.TryGetValue(systemKey, out var d) ? d : null;
        AvatarUrl? avatarUrl = _avatarBySystem.TryGetValue(systemKey, out var a) ? a : null;
        AvatarSource? avatarSource = _avatarSourceBySystem.TryGetValue(systemKey, out var s) ? s : null;
        DiscordId? discordId = _discordBySystem.TryGetValue(systemKey, out var discord) ? discord : null;
        Email? email = _emailBySystem.TryGetValue(systemKey, out var e) ? e : null;
        AppleId? appleId = _appleBySystem.TryGetValue(systemKey, out var apple) ? apple : null;

        if (username is null && description is null && avatarUrl is null && discordId is null && email is null && appleId is null)
        {
            return Task.FromResult<AccountPublicProfileReadModel?>(null);
        }

        return Task.FromResult<AccountPublicProfileReadModel?>(
            new AccountPublicProfileReadModel(
                systemId,
                username,
                description,
                avatarUrl,
                avatarSource,
                discordId,
                email,
                appleId));
    }

    private string GetSystemKey(SystemId systemId) => InMemoryStorageKeys.ForSystem(_regionContext, systemId);

    private ScopedSystemId ResolveScoped(SystemId systemId)
        => ScopedSystemId.Compose(_regionContext.ResolveUserRegion(systemId), systemId);

    /// <summary>
    /// Drop a link-token from both maps. Used by the two read paths that discover a stale
    /// or missing entry — the deterministic-token hash means we cannot rely on a
    /// re-issued token to bury the stale mapping, so lazy scrub on read is the guardrail
    /// against dangling reverse-map pointers (Slice 4 bug B).
    /// Round-2 Commit 12: linkTokenValue parameter promoted to <see cref="LinkToken"/>?
    /// so callers hand the typed wrapper directly; the record-struct ordinal equality on
    /// the underlying string preserves byte-compat with the pre-Round-2 string.Equals compare.
    /// </summary>
    private void ScrubLinkToken(string? systemKey = null, LinkToken? linkTokenValue = null)
    {
        if (linkTokenValue is { } token)
        {
            _systemByLinkToken.TryRemove(token, out _);
            // Also drop the systemKey → token pointer if it still references this token
            // (deterministic-hash tokens make this cheap; no scan of the entire dictionary).
            foreach (var kvp in _linkTokenBySystem)
            {
                if (kvp.Value == token)
                {
                    _linkTokenBySystem.TryRemove(kvp.Key, out _);
                    break;
                }
            }
        }

        if (systemKey is not null && _linkTokenBySystem.TryRemove(systemKey, out var storedToken))
        {
            _systemByLinkToken.TryRemove(storedToken, out _);
        }
    }

    /// <summary>
    /// Round-2 Commit 12: promoted from `LinkIdentifier(..., string, ConcurrentDictionary&lt;string, string&gt;, ...)` to a generic-typed helper that
    /// speaks the wrapper types directly. <typeparamref name="TIdentity"/> is one of
    /// <see cref="DiscordId"/>, <see cref="Email"/>, <see cref="AppleId"/>; the
    /// <paramref name="extractRawValue"/> accessor pulls the underlying string only where
    /// it's needed for the reverse-map key (which stays string-typed to keep the
    /// case-insensitive <see cref="StringComparer.OrdinalIgnoreCase"/> semantics for
    /// email-and-friends). The three call-sites feed static lambdas so there is no
    /// allocation per call.
    /// </summary>
    private AccountLinkResult LinkIdentifier<TIdentity>(
        SystemId systemId,
        TIdentity identifier,
        ConcurrentDictionary<string, TIdentity> identifierBySystem,
        ConcurrentDictionary<string, ScopedSystemId> systemByIdentifier,
        Func<TIdentity, string> extractRawValue)
        where TIdentity : struct
    {
        var rawValue = extractRawValue(identifier);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return AccountLinkResult.UserNotFound;
        }

        var systemKey = GetSystemKey(systemId);
        var scopedSystemId = ResolveScoped(systemId);

        if (_usernameBySystem.ContainsKey(systemKey) is false &&
            _descriptionBySystem.ContainsKey(systemKey) is false &&
            _avatarBySystem.ContainsKey(systemKey) is false &&
            _linkTokenBySystem.ContainsKey(systemKey) is false)
        {
            return AccountLinkResult.UserNotFound;
        }

        if (identifierBySystem.TryGetValue(systemKey, out var existing)
            && !string.IsNullOrWhiteSpace(extractRawValue(existing)))
        {
            return AccountLinkResult.AlreadyLinked;
        }

        if (systemByIdentifier.TryGetValue(rawValue, out var owner) && owner != scopedSystemId)
        {
            return AccountLinkResult.UserExists;
        }

        identifierBySystem[systemKey] = identifier;
        systemByIdentifier[rawValue] = scopedSystemId;
        return AccountLinkResult.Success;
    }

    /// <summary>
    /// Seed a per-system encryption salt at first-touch by the FindOrCreate paths.
    ///
    /// <para>
    /// Round-2 Commit 9 (canvas #21): retyped the parameter from <c>string
    /// scopedSystemId</c> to <see cref="ScopedSystemId"/>. Pre-Round-2 the three call
    /// sites all owned a typed <see cref="ScopedSystemId"/> local and had to unwrap it
    /// to <c>.Value</c> at the boundary, only for this method to re-wrap it via
    /// <c>new SystemId(scopedSystemId)</c> for the <see cref="IEncryptionStateRepository.UpsertAsync"/>
    /// call. The wrapper eliminates that string round-trip and pins the "the caller
    /// already resolved this to a scoped composite" invariant at the type level.
    /// </para>
    /// </summary>
    private void EnsureEncryptionSaltForSystem(ScopedSystemId scoped)
    {
        if (_encryptionStates is null)
            return;

        var saltBytes = RandomNumberGenerator.GetBytes(32);
        var salt = Convert.ToBase64String(saltBytes);
        _ = _encryptionStates.UpsertAsync(scoped.AsSystemId(), false, null, new EncryptionSalt(salt), CancellationToken.None);
    }
}
