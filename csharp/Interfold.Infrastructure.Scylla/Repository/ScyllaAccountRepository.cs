using System.Collections.Concurrent;
using Cassandra;
using System.Security.Cryptography;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaAccountRepository : IAccountRepository
{
    private readonly record struct LinkTokenEntry(string ScopedSystemId, DateTimeOffset ExpiresAt);

    private static readonly TimeSpan LinkTokenTtl = TimeSpan.FromMinutes(5);
    private readonly object _linkTokenLock = new();
    private readonly ConcurrentDictionary<string, string> _linkTokenBySystem = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LinkTokenEntry> _systemByLinkToken = new(StringComparer.Ordinal);

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaAccountRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options
    )
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
    }

    public async Task<bool> UpdateUsernameAsync(SystemId systemId, Username username, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            // Read old username to maintain lookup table
            var oldRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT username FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();
            var oldUsername = oldRow?.GetValue<string?>("username");

            var batch = new BatchStatement();
            if (oldRow is null)
            {
                // First touch for a JWT-scoped principal: mint the regional users row so
                // GetPublicProfileAsync (and public guarded reads that gate on it) succeed.
                // InMemory achieves the same implicitly by writing into its username map;
                // Scylla previously only issued UPDATE, leaving ShowAlter to 404 system_not_found.
                batch.Add(new SimpleStatement(
                    $"INSERT INTO {keyspace}.users (id, username, inserted_at, updated_at) VALUES (?, ?, toTimestamp(now()), toTimestamp(now()))",
                    normalizedSystemId,
                    username.Value));
                batch.Add(new SimpleStatement(
                    $"INSERT INTO {ScyllaGlobalKeyspace.Name}.user_registry (user_id, username, region, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                    normalizedSystemId,
                    username.Value,
                    keyspace));
            }
            else
            {
                batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.users SET username = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                    username.Value, normalizedSystemId));
                batch.Add(new SimpleStatement(
                    $"UPDATE {ScyllaGlobalKeyspace.Name}.user_registry SET username = ?, updated_at = toTimestamp(now()) WHERE user_id = ?",
                    username.Value, normalizedSystemId));
            }

            // Remove old lookup entry
            if (!string.IsNullOrWhiteSpace(oldUsername))
            {
                batch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.users_by_username WHERE username = ?", oldUsername));
                batch.Add(new SimpleStatement(
                    $"DELETE FROM {ScyllaGlobalKeyspace.Name}.user_registry_by_username WHERE username = ?", oldUsername));
            }

            // Insert new lookup entry
            if (!string.IsNullOrWhiteSpace(username.Value))
            {
                batch.Add(new SimpleStatement(
                    $"INSERT INTO {keyspace}.users_by_username (username, user_id) VALUES (?, ?)",
                    username.Value, normalizedSystemId));
                batch.Add(new SimpleStatement(
                    $"INSERT INTO {ScyllaGlobalKeyspace.Name}.user_registry_by_username (username, user_id, region) VALUES (?, ?, ?)",
                    username.Value, normalizedSystemId, keyspace));
            }

            await session.ExecuteAsync(batch);
            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateDescriptionAsync(SystemId systemId, string description, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var statement = new SimpleStatement(
                $"UPDATE {keyspace}.users SET description = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                description,
                normalizedSystemId
            );

            await session.ExecuteAsync(statement);
            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateAvatarAsync(SystemId systemId, AvatarUrl avatarUrl, AvatarSource source, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var statement = new SimpleStatement(
                $"UPDATE {keyspace}.users SET avatar_url = ?, avatar_source = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                avatarUrl.Value,
                source.ToCode(),
                normalizedSystemId
            );

            await session.ExecuteAsync(statement);
            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> ClearAvatarAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            // Null both columns together so observers can never see a half-cleared state.
            var statement = new SimpleStatement(
                $"UPDATE {keyspace}.users SET avatar_url = ?, avatar_source = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                null,
                null,
                normalizedSystemId
            );

            await session.ExecuteAsync(statement);
            return true;
        }, _options, cancellationToken);
    }

    public Task<LinkToken> GetOrCreateLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
        var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
        // Compose is idempotent on already-scoped inputs; the extra safety net is why we're
        // routing every partition-key composition through it in Slice 4.
        var scopedSystemId = ScopedSystemId.Compose(keyspace, normalizedSystemId).Value;
        var systemKey = scopedSystemId;
        var now = DateTimeOffset.UtcNow;

        lock (_linkTokenLock)
        {
            if (_linkTokenBySystem.TryGetValue(systemKey, out var existingToken)
                && _systemByLinkToken.TryGetValue(existingToken, out var existingEntry)
                && existingEntry.ExpiresAt > now)
            {
                return Task.FromResult(new LinkToken(existingToken));
            }

            if (!string.IsNullOrWhiteSpace(existingToken))
            {
                _linkTokenBySystem.TryRemove(systemKey, out _);
                _systemByLinkToken.TryRemove(existingToken, out _);
            }

            var token = Guid.NewGuid().ToString();
            _linkTokenBySystem[systemKey] = token;
            _systemByLinkToken[token] = new LinkTokenEntry(scopedSystemId, now.Add(LinkTokenTtl));

            return Task.FromResult(new LinkToken(token));
        }
    }

    public Task<LinkToken?> GetLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
        var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
        var systemKey = ScopedSystemId.Compose(keyspace, normalizedSystemId).Value;
        var now = DateTimeOffset.UtcNow;

        lock (_linkTokenLock)
        {
            if (_linkTokenBySystem.TryGetValue(systemKey, out var token)
                && _systemByLinkToken.TryGetValue(token, out var entry)
                && entry.ExpiresAt > now)
            {
                return Task.FromResult<LinkToken?>(new LinkToken(token));
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                _linkTokenBySystem.TryRemove(systemKey, out _);
                _systemByLinkToken.TryRemove(token, out _);
            }

            return Task.FromResult<LinkToken?>(null);
        }
    }

    public Task<SystemId?> ResolveSystemIdByLinkTokenAsync(LinkToken linkToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(linkToken.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        var now = DateTimeOffset.UtcNow;
        lock (_linkTokenLock)
        {
            if (_systemByLinkToken.TryGetValue(linkToken.Value, out var entry) && entry.ExpiresAt > now)
            {
                return Task.FromResult<SystemId?>(new SystemId(entry.ScopedSystemId));
            }

            _systemByLinkToken.TryRemove(linkToken.Value, out _);
            foreach (var item in _linkTokenBySystem)
            {
                if (string.Equals(item.Value, linkToken.Value, StringComparison.Ordinal))
                {
                    _linkTokenBySystem.TryRemove(item.Key, out _);
                    break;
                }
            }

            return Task.FromResult<SystemId?>(null);
        }
    }

    public Task<bool> ClearLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
        var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
        var systemKey = ScopedSystemId.Compose(keyspace, normalizedSystemId).Value;

        lock (_linkTokenLock)
        {
            if (_linkTokenBySystem.TryRemove(systemKey, out var token))
            {
                _systemByLinkToken.TryRemove(token, out _);
            }

            return Task.FromResult(true);
        }
    }

    public Task<SystemId?> TryFindSystemIdByDiscordIdAsync(DiscordId discordId, CancellationToken cancellationToken = default)
        => TryFindSystemIdByRegistryColumnAsync("discord_id", discordId.Value, cancellationToken);

    public Task<SystemId?> FindOrCreateSystemIdByDiscordIdAsync(DiscordId discordId, CancellationToken cancellationToken = default)
        => FindOrCreateSystemIdByRegistryColumnAsync("discord_id", discordId.Value, cancellationToken);

    public Task<SystemId?> FindSystemIdByEmailAsync(Email email, CancellationToken cancellationToken = default)
        => FindOrCreateSystemIdByRegistryColumnAsync("email", email.Value, cancellationToken);

    public Task<SystemId?> FindSystemIdByAppleIdAsync(AppleId appleId, CancellationToken cancellationToken = default)
        => FindOrCreateSystemIdByRegistryColumnAsync("apple_id", appleId.Value, cancellationToken);

    public Task<AccountLinkResult> LinkDiscordToUserAsync(SystemId systemId, DiscordId discordId, CancellationToken cancellationToken = default)
        => LinkIdentityAsync(systemId, "discord_id", discordId.Value, cancellationToken);

    public Task<AccountLinkResult> LinkEmailToUserAsync(SystemId systemId, Email email, CancellationToken cancellationToken = default)
        => LinkIdentityAsync(systemId, "email", email.Value, cancellationToken);

    public Task<AccountLinkResult> LinkAppleToUserAsync(SystemId systemId, AppleId appleId, CancellationToken cancellationToken = default)
        => LinkIdentityAsync(systemId, "apple_id", appleId.Value, cancellationToken);

    public Task<bool> UnlinkDiscordAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => UnlinkIdentityAsync(systemId, "discord_id", cancellationToken);

    public Task<bool> UnlinkEmailAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => UnlinkIdentityAsync(systemId, "email", cancellationToken);

    public Task<bool> UnlinkAppleAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => UnlinkIdentityAsync(systemId, "apple_id", cancellationToken);

    public async Task<bool> DeleteAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            // Fetch identity fields to clean up lookup tables
            var userRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, discord_id, email, username, apple_id, google_id FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();

            if (userRow is null)
            {
                return true;
            }
            
            var deleteBatch = new BatchStatement();
            deleteBatch.Add(new SimpleStatement($"DELETE FROM {keyspace}.users WHERE id = ?", normalizedSystemId));
            deleteBatch.Add(new SimpleStatement($"DELETE FROM {ScyllaGlobalKeyspace.Name}.user_registry WHERE user_id = ?", normalizedSystemId));

            // Clean up denormalized identity lookup tables
            var identityColumns = new[] { "discord_id", "email", "username", "apple_id", "google_id" };
            foreach (var col in identityColumns)
            {
                var value = userRow.GetValue<string?>(col);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    deleteBatch.Add(new SimpleStatement($"DELETE FROM {keyspace}.users_by_{col} WHERE {col} = ?", value));
                    deleteBatch.Add(new SimpleStatement($"DELETE FROM {ScyllaGlobalKeyspace.Name}.user_registry_by_{col} WHERE {col} = ?", value));
                }
            }

            await session.ExecuteAsync(deleteBatch);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var profileQuery = new SimpleStatement(
                $"SELECT username, avatar_url, avatar_source, description, discord_id, email, apple_id FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            var profile = (await session.ExecuteAsync(profileQuery)).FirstOrDefault();
            if (profile is null)
            {
                return null;
            }

            return new AccountPublicProfileReadModel(
                new SystemId(normalizedSystemId),
                profile.GetValue<string?>("username") is { } username ? new Username(username) : null,
                profile.GetValue<string?>("description"),
                AvatarUrl.FromNullable(profile.GetValue<string?>("avatar_url")),
                AvatarSourceExtensions.TryFromCode(profile.GetValue<short?>("avatar_source")),
                profile.GetValue<string?>("discord_id") is { } discordId ? new DiscordId(discordId) : null,
                profile.GetValue<string?>("email") is { } email ? new Email(email) : null,
                profile.GetValue<string?>("apple_id") is { } appleId ? new AppleId(appleId) : null);
        }, _options, cancellationToken);
    }

    // Returns the scoped `{region}:{userId}` composite wrapped in a SystemId — this file's
    // in-process caches and downstream callers keep operating in the scoped-composite
    // shape until Slice 4 introduces a dedicated value object. The typing here just plugs
    // the string leak at the SystemId? boundary the interface promises.
    private async Task<SystemId?> TryFindSystemIdByRegistryColumnAsync(string columnName, string value, CancellationToken cancellationToken)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync<SystemId?>(async () =>
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var query = new SimpleStatement(
                $"SELECT user_id, region FROM {ScyllaGlobalKeyspace.Name}.user_registry_by_{columnName} WHERE {columnName} = ? LIMIT 1",
                value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is not null)
            {
                var userId = NormalizeRegistryUserId(row.GetValue<string>("user_id"));
                var region = row.GetValue<string?>("region") ?? _keyspaceResolver.DefaultKeyspace;
                return ScopedSystemId.Compose(region, userId).AsSystemId();
            }

            return null;
        }, _options, cancellationToken);
    }

    private async Task<SystemId?> FindOrCreateSystemIdByRegistryColumnAsync(string columnName, string value, CancellationToken cancellationToken)
    {
        var existing = await TryFindSystemIdByRegistryColumnAsync(columnName, value, cancellationToken);
        if (existing is { } typedExisting && !string.IsNullOrWhiteSpace(typedExisting.Value))
        {
            return typedExisting;
        }

        return await DatabaseTransientRetry.ExecuteScyllaAsync<SystemId?>(async () =>
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);

            const string idChars = "abcdefghijklmnopqrstuvwxyz";

            // User doesn't exist; auto-create new account
            var newRegion = _keyspaceResolver.DefaultKeyspace; //TODO: Maybe look into geoip-based region resolution here instead of just defaulting?
            var newUserId = Random.Shared.GetString(idChars, 7);
            var keyspace = newRegion; // ResolveRegionalKeyspace would just return the newRegion

            // Write regional user + global registry together to reduce split-write orphans.
            var createUserBatch = new BatchStatement()
                .Add(new SimpleStatement(
                    // Four columns -> four bind targets: two prepared parameters plus two literal
                    // toTimestamp(now()) calls. Adding a third placeholder before the literals (the
                    // shape this method had previously) tipped the value count over the column
                    // count and Cassandra rejected the batch with "Unmatched column names/values".
                    $"INSERT INTO {keyspace}.users (id, {columnName}, inserted_at, updated_at) VALUES (?, ?, toTimestamp(now()), toTimestamp(now()))",
                    newUserId,
                    value
                ))
                .Add(new SimpleStatement(
                    $"INSERT INTO {ScyllaGlobalKeyspace.Name}.user_registry (user_id, {columnName}, region, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                    newUserId,
                    value,
                    newRegion
                ))
                // Maintain denormalized lookup tables
                .Add(new SimpleStatement(
                    $"INSERT INTO {keyspace}.users_by_{columnName} ({columnName}, user_id) VALUES (?, ?)",
                    value,
                    newUserId
                ))
                .Add(new SimpleStatement(
                    $"INSERT INTO {ScyllaGlobalKeyspace.Name}.user_registry_by_{columnName} ({columnName}, user_id, region) VALUES (?, ?, ?)",
                    value,
                    newUserId,
                    newRegion
                ));

            await session.ExecuteAsync(createUserBatch);

            return ScopedSystemId.Compose(newRegion, newUserId).AsSystemId();
        }, _options, cancellationToken);
    }

    private string NormalizeRegistryUserId(string userId)
    {
        var normalized = userId;
        for (var i = 0; i < 3; i++)
        {
            var next = _keyspaceResolver.NormalizeSystemId(normalized);
            if (string.Equals(next, normalized, StringComparison.Ordinal))
            {
                break;
            }

            normalized = next;
        }

        return normalized;
    }

    private async Task<AccountLinkResult> LinkIdentityAsync(SystemId systemId, string columnName, string value, CancellationToken cancellationToken)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return AccountLinkResult.UserNotFound;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var owner = await TryFindSystemIdByRegistryColumnAsync(columnName, value, cancellationToken);
            if (owner is { } typedOwner && !string.IsNullOrWhiteSpace(typedOwner.Value))
            {
                var normalizedOwner = NormalizeRegistryUserId(_keyspaceResolver.NormalizeSystemId(typedOwner));
                if (!string.Equals(normalizedOwner, normalizedSystemId, StringComparison.Ordinal))
                {
                    return AccountLinkResult.UserExists;
                }
            }

            var userRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, {columnName} FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();

            if (userRow is null)
            {
                return AccountLinkResult.UserNotFound;
            }

            var alreadyLinked = userRow.GetValue<string?>(columnName);
            if (!string.IsNullOrWhiteSpace(alreadyLinked))
            {
                return AccountLinkResult.AlreadyLinked;
            }

            var linkBatch = new BatchStatement();
            linkBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.users SET {columnName} = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                value,
                normalizedSystemId));
            linkBatch.Add(new SimpleStatement(
                $"UPDATE {ScyllaGlobalKeyspace.Name}.user_registry SET {columnName} = ?, updated_at = toTimestamp(now()) WHERE user_id = ?",
                value,
                normalizedSystemId));
            // Maintain denormalized lookup tables
            linkBatch.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.users_by_{columnName} ({columnName}, user_id) VALUES (?, ?)",
                value,
                normalizedSystemId));
            linkBatch.Add(new SimpleStatement(
                $"INSERT INTO {ScyllaGlobalKeyspace.Name}.user_registry_by_{columnName} ({columnName}, user_id, region) VALUES (?, ?, ?)",
                value,
                normalizedSystemId,
                keyspace));
            await session.ExecuteAsync(linkBatch);

            return AccountLinkResult.Success;
        }, _options, cancellationToken);
    }

    private async Task<bool> UnlinkIdentityAsync(SystemId systemId, string columnName, CancellationToken cancellationToken)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            // Read old value to delete from lookup tables
            var oldRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT {columnName} FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();
            var oldValue = oldRow?.GetValue<string?>(columnName);

            var unlinkBatch = new BatchStatement();
            unlinkBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.users SET {columnName} = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                null,
                normalizedSystemId));
            unlinkBatch.Add(new SimpleStatement(
                $"UPDATE {ScyllaGlobalKeyspace.Name}.user_registry SET {columnName} = ?, updated_at = toTimestamp(now()) WHERE user_id = ?",
                null,
                normalizedSystemId));

            // Remove from denormalized lookup tables if old value existed
            if (!string.IsNullOrWhiteSpace(oldValue))
            {
                unlinkBatch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.users_by_{columnName} WHERE {columnName} = ?",
                    oldValue));
                unlinkBatch.Add(new SimpleStatement(
                    $"DELETE FROM {ScyllaGlobalKeyspace.Name}.user_registry_by_{columnName} WHERE {columnName} = ?",
                    oldValue));
            }

            await session.ExecuteAsync(unlinkBatch);
            return true;
        }, _options, cancellationToken);
    }

}
