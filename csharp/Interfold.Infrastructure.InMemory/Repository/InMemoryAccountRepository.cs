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
    private readonly ConcurrentDictionary<string, string> _usernameBySystem = new();
    private readonly ConcurrentDictionary<string, string> _descriptionBySystem = new();
    private readonly ConcurrentDictionary<string, string> _avatarBySystem = new();
    private readonly ConcurrentDictionary<string, AvatarSource> _avatarSourceBySystem = new();
    private readonly ConcurrentDictionary<string, string> _linkTokenBySystem = new();
    private readonly ConcurrentDictionary<string, string> _systemByLinkToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _discordBySystem = new();
    private readonly ConcurrentDictionary<string, string> _emailBySystem = new();
    private readonly ConcurrentDictionary<string, string> _appleBySystem = new();
    private readonly ConcurrentDictionary<string, string> _systemByDiscord = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _systemByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _systemByApple = new(StringComparer.OrdinalIgnoreCase);

    private readonly IEncryptionStateRepository? _encryptionStates;
    private readonly IRegionContext _regionContext;

    public InMemoryAccountRepository(IRegionContext regionContext, IEncryptionStateRepository? encryptionStates = null)
    {
        _regionContext = regionContext;
        _encryptionStates = encryptionStates;
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
        var token = _linkTokenBySystem.GetOrAdd(systemKey, static key =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hash)[..32].ToLowerInvariant();
        });

        _systemByLinkToken[token] = _regionContext.ResolveUserRegion(systemId).ToWireValue() + ":" + systemId.Value;

        return Task.FromResult(new LinkToken(token));
    }

    public Task<LinkToken?> GetLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _linkTokenBySystem.TryGetValue(systemKey, out var token);
        return Task.FromResult<LinkToken?>(token is null ? null : new LinkToken(token));
    }

    public Task<SystemId?> ResolveSystemIdByLinkTokenAsync(LinkToken linkToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(linkToken.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        _systemByLinkToken.TryGetValue(linkToken.Value, out var scopedSystemId);
        return Task.FromResult<SystemId?>(scopedSystemId is null ? null : new SystemId(scopedSystemId));
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

    public Task<SystemId?> FindSystemIdByDiscordIdAsync(DiscordId discordId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(discordId.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        if (_systemByDiscord.TryGetValue(discordId.Value, out var scopedSystemId))
        {
            return Task.FromResult<SystemId?>(new SystemId(scopedSystemId));
        }

        // Auto-create new system
        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNewSystemId = _regionContext.ResolveUserRegion(newSystemId).ToWireValue() + ":" + newSystemId;
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

        // Auto-create new system
        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNewSystemId = _regionContext.ResolveUserRegion(newSystemId).ToWireValue() + ":" + newSystemId;
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

        // Auto-create new system
        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNewSystemId = _regionContext.ResolveUserRegion(newSystemId).ToWireValue() + ":" + newSystemId;
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
        _linkTokenBySystem.TryRemove(systemKey, out _);

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
        var scopedSystemId = _regionContext.ResolveUserRegion(systemId).ToWireValue() + ":" + systemId.Value;

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
