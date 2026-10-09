using System.Security.Cryptography;
using System.Text;
using Dapper;
using Interfold.Auth.Contracts.Ids;
using Interfold.Settings.Contracts.Ids;
using Interfold.Infrastructure.Sqlite;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Systems.Contracts.Models.Read;
using Microsoft.Data.Sqlite;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteAccountRepository : IAccountRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IEncryptionStateRepository _encryptionStates;
    private readonly TimeProvider _timeProvider;

    public SqliteAccountRepository(
        ISqliteConnectionFactory connectionFactory,
        IEncryptionStateRepository encryptionStates,
        TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _encryptionStates = encryptionStates;
        _timeProvider = timeProvider;
    }

    public async Task EnsureExistsAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        int inserted;
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken))
        {
            inserted = await connection.ExecuteAsync(
                """
                INSERT OR IGNORE INTO accounts (system_id, created_at, updated_at)
                VALUES (@system_id, @now, @now)
                """,
                new { system_id = systemId, now = nowMs });
        }

        if (inserted > 0)
        {
            await _encryptionStates.UpsertAsync(systemId, false, null, EncryptionSalt.NewRandom(), cancellationToken);
        }
    }

    public async Task<bool> UpdateUsernameAsync(SystemId systemId, Username username, CancellationToken cancellationToken = default)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(
            """
            UPDATE accounts
            SET username = @value, updated_at = @now
            WHERE system_id = @system_id
            """,
            new { system_id = systemId, value = username.Value, now = nowMs });
        return rows > 0;
    }

    public async Task<bool> UpdateDescriptionAsync(SystemId systemId, string description, CancellationToken cancellationToken = default)
    {
        await EnsureExistsAsync(systemId, cancellationToken);
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(
            """
            UPDATE accounts
            SET description = @value, updated_at = @now
            WHERE system_id = @system_id
            """,
            new { system_id = systemId, value = description, now = nowMs });
        return rows > 0;
    }

    public async Task<bool> UpdateAvatarAsync(
        SystemId systemId,
        AvatarUrl avatarUrl,
        AvatarSource source,
        CancellationToken cancellationToken = default)
    {
        await EnsureExistsAsync(systemId, cancellationToken);
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(
            """
            UPDATE accounts
            SET avatar_url = @avatar_url, avatar_source = @avatar_source, updated_at = @now
            WHERE system_id = @system_id
            """,
            new
            {
                system_id = systemId,
                avatar_url = avatarUrl.Value,
                avatar_source = (short)source,
                now = nowMs,
            });
        return rows > 0;
    }

    public async Task<bool> ClearAvatarAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        await EnsureExistsAsync(systemId, cancellationToken);
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(
            """
            UPDATE accounts
            SET avatar_url = NULL, avatar_source = NULL, updated_at = @now
            WHERE system_id = @system_id
            """,
            new { system_id = systemId, now = nowMs });
        return rows > 0;
    }

    public async Task<LinkToken> GetOrCreateLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var expiresAt = now.Add(LinkToken.Ttl).ToUnixTimeMilliseconds();
        var nowMs = now.ToUnixTimeMilliseconds();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(systemId));
        var tokenValue = Convert.ToHexString(hash)[..32].ToLowerInvariant();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(
            """
            UPDATE accounts
            SET link_token = @link_token, link_token_expires_at = @expires_at, updated_at = @now
            WHERE system_id = @system_id
            """,
            new { system_id = systemId, link_token = tokenValue, expires_at = expiresAt, now = nowMs });
        if (rows == 0)
        {
            throw new InvalidOperationException("Account does not exist.");
        }

        return new LinkToken(tokenValue);
    }

    public async Task<LinkToken?> GetLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<LinkTokenRow>(
            """
            SELECT link_token AS LinkToken, link_token_expires_at AS ExpiresAt
            FROM accounts
            WHERE system_id = @system_id
            LIMIT 1
            """,
            new { system_id = systemId });
        if (row is null || string.IsNullOrWhiteSpace(row.LinkToken))
        {
            return null;
        }

        if (row.ExpiresAt > nowMs)
        {
            return new LinkToken(row.LinkToken);
        }

        await ScrubLinkTokenAsync(connection, systemKey: systemId, cancellationToken: cancellationToken);
        return null;
    }

    public async Task<SystemId?> ResolveSystemIdByLinkTokenAsync(LinkToken linkToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(linkToken.Value))
        {
            return null;
        }

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<LinkTokenOwnerRow>(
            """
            SELECT system_id AS SystemId, link_token_expires_at AS ExpiresAt
            FROM accounts
            WHERE link_token = @link_token
            LIMIT 1
            """,
            new { link_token = linkToken.Value });
        if (row is null)
        {
            return null;
        }

        if (row.ExpiresAt > nowMs)
        {
            return row.SystemId;
        }

        await ScrubLinkTokenAsync(connection, linkTokenValue: linkToken.Value, cancellationToken: cancellationToken);
        return null;
    }

    public async Task<bool> ClearLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await ScrubLinkTokenAsync(
            connection,
            systemKey: systemId,
            cancellationToken: cancellationToken);
        return rows > 0;
    }

    public Task<SystemId?> TryFindSystemIdByDiscordIdAsync(DiscordId discordId, CancellationToken cancellationToken = default)
        => TryFindByDiscordAsync(discordId.Value, cancellationToken);

    public Task<SystemId?> FindOrCreateSystemIdAsync(ProviderIdentity identity, CancellationToken cancellationToken = default)
        => identity.MatchOrThrow(
            discordId => FindOrCreateByProviderAsync(
                TryFindByDiscordAsync,
                """
                INSERT INTO accounts (system_id, discord_id, created_at, updated_at)
                VALUES (@system_id, @value, @now, @now)
                """,
                discordId.Value,
                cancellationToken),
            email => FindOrCreateByProviderAsync(
                TryFindByEmailAsync,
                """
                INSERT INTO accounts (system_id, email, created_at, updated_at)
                VALUES (@system_id, @value, @now, @now)
                """,
                email.Value,
                cancellationToken),
            appleId => FindOrCreateByProviderAsync(
                TryFindByAppleAsync,
                """
                INSERT INTO accounts (system_id, apple_id, created_at, updated_at)
                VALUES (@system_id, @value, @now, @now)
                """,
                appleId.Value,
                cancellationToken));

    public Task<AccountLinkResult> LinkIdentityToUserAsync(
        SystemId systemId,
        ProviderIdentity identity,
        CancellationToken cancellationToken = default)
        => identity.MatchOrThrow(
            discordId => LinkProviderAsync(
                systemId,
                discordId.Value,
                row => row.DiscordId,
                TryFindByDiscordAsync,
                """
                UPDATE accounts SET discord_id = @value, updated_at = @now WHERE system_id = @system_id
                """,
                cancellationToken),
            email => LinkProviderAsync(
                systemId,
                email.Value,
                row => row.Email,
                TryFindByEmailAsync,
                """
                UPDATE accounts SET email = @value, updated_at = @now WHERE system_id = @system_id
                """,
                cancellationToken),
            appleId => LinkProviderAsync(
                systemId,
                appleId.Value,
                row => row.AppleId,
                TryFindByAppleAsync,
                """
                UPDATE accounts SET apple_id = @value, updated_at = @now WHERE system_id = @system_id
                """,
                cancellationToken));

    public Task<bool> UnlinkDiscordAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => UnlinkColumnAsync(
            systemId,
            """
            UPDATE accounts SET discord_id = NULL, updated_at = @now WHERE system_id = @system_id
            """,
            cancellationToken);

    public Task<bool> UnlinkEmailAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => UnlinkColumnAsync(
            systemId,
            """
            UPDATE accounts SET email = NULL, updated_at = @now WHERE system_id = @system_id
            """,
            cancellationToken);

    public Task<bool> UnlinkAppleAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => UnlinkColumnAsync(
            systemId,
            """
            UPDATE accounts SET apple_id = NULL, updated_at = @now WHERE system_id = @system_id
            """,
            cancellationToken);

    public async Task<bool> DeleteAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        await using var work = await SqliteWork.OpenAsync(_connectionFactory, cancellationToken);
        var rows = await work.Connection.ExecuteAsync(
            "DELETE FROM accounts WHERE system_id = @system_id",
            new { system_id = systemId },
            work.Transaction);
        await work.CommitAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<SystemId?> TryFindSystemIdByUsernameAsync(
        Username username,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var persisted = await connection.QueryFirstOrDefaultAsync<string>(
            """
            SELECT system_id
            FROM accounts
            WHERE username = @username COLLATE NOCASE
            LIMIT 1
            """,
            new { username = username.Value });
        return persisted is null ? null : new SystemId(persisted);
    }

    public async Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        var row = await LoadAccountRowAsync(systemId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (row.Username is null
            && row.Description is null
            && row.AvatarUrl is null
            && row.DiscordId is null
            && row.Email is null
            && row.AppleId is null)
        {
            return null;
        }

        return new AccountPublicProfileReadModel(
            systemId,
            row.Username is { } u ? new Username(u) : null,
            row.Description,
            AvatarUrl.FromNullable(row.AvatarUrl),
            row.AvatarSource is { } src ? ((short)src).FromCodeOrNull<AvatarSource>() : null,
            row.DiscordId is { } d ? new DiscordId(d) : null,
            row.Email is { } e ? new Email(e) : null,
            row.AppleId is { } a ? new AppleId(a) : null);
    }

    public async Task<PublicSystemReadModel?> GetPublicSystemAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        var row = await LoadAccountRowAsync(systemId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var hasIdentity = row.Username is not null
            || row.Description is not null
            || row.AvatarUrl is not null
            || row.DiscordId is not null
            || row.Email is not null
            || row.AppleId is not null;

        if (!hasIdentity)
        {
            return null;
        }

        return new PublicSystemReadModel(
            Id: systemId,
            AvatarUrl: AvatarUrl.FromNullable(row.AvatarUrl),
            AvatarSource: row.AvatarSource is { } src ? ((short)src).FromCodeOrNull<AvatarSource>() : null,
            Username: row.Username is { } u ? new Username(u) : null,
            Description: row.Description);
    }

    private Task<SystemId?> TryFindByDiscordAsync(string value, CancellationToken cancellationToken)
        => TryFindByProviderAsync(
            """
            SELECT system_id FROM accounts WHERE discord_id = @value COLLATE NOCASE LIMIT 1
            """,
            value,
            cancellationToken);

    private Task<SystemId?> TryFindByEmailAsync(string value, CancellationToken cancellationToken)
        => TryFindByProviderAsync(
            """
            SELECT system_id FROM accounts WHERE email = @value COLLATE NOCASE LIMIT 1
            """,
            value,
            cancellationToken);

    private Task<SystemId?> TryFindByAppleAsync(string value, CancellationToken cancellationToken)
        => TryFindByProviderAsync(
            """
            SELECT system_id FROM accounts WHERE apple_id = @value COLLATE NOCASE LIMIT 1
            """,
            value,
            cancellationToken);

    private async Task<SystemId?> TryFindByProviderAsync(string sql, string value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var persisted = await connection.QueryFirstOrDefaultAsync<string>(sql, new { value });
        return persisted is null ? null : new SystemId(persisted);
    }

    private async Task<SystemId?> FindOrCreateByProviderAsync(
        Func<string, CancellationToken, Task<SystemId?>> tryFind,
        string insertSql,
        string value,
        CancellationToken cancellationToken)
    {
        var existing = await tryFind(value, cancellationToken);
        if (existing is { } typed && !string.IsNullOrWhiteSpace(typed.Value))
        {
            return typed;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var rawId = Guid.NewGuid().ToString("N");
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        try
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(insertSql, new { system_id = rawId, value, now = nowMs });
        }
        catch (SqliteException)
        {
            return await tryFind(value, cancellationToken);
        }

        var wired = new SystemId(rawId);
        await _encryptionStates.UpsertAsync(wired, false, null, EncryptionSalt.NewRandom(), cancellationToken);
        return wired;
    }

    private async Task<AccountLinkResult> LinkProviderAsync(
        SystemId systemId,
        string value,
        Func<AccountRow, string?> existingSelector,
        Func<string, CancellationToken, Task<SystemId?>> tryFind,
        string updateSql,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AccountLinkResult.UserNotFound;
        }

        var row = await LoadAccountRowAsync(systemId, cancellationToken);
        if (row is null)
        {
            return AccountLinkResult.UserNotFound;
        }

        if (!string.IsNullOrWhiteSpace(existingSelector(row)))
        {
            return AccountLinkResult.AlreadyLinked;
        }

        var owner = await tryFind(value, cancellationToken);
        if (owner is { } typedOwner
            && !string.IsNullOrWhiteSpace(typedOwner.Value)
            && !string.Equals(typedOwner, systemId, StringComparison.Ordinal))
        {
            return AccountLinkResult.UserExists;
        }

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(updateSql, new { value, now = nowMs, system_id = systemId });
        return AccountLinkResult.Success;
    }

    private async Task<bool> UnlinkColumnAsync(SystemId systemId, string sql, CancellationToken cancellationToken)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(sql, new { now = nowMs, system_id = systemId });
        return rows > 0;
    }

    private async Task<int> ScrubLinkTokenAsync(
        SqliteConnection connection,
        string? systemKey = null,
        string? linkTokenValue = null,
        CancellationToken cancellationToken = default)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var rows = 0;
        if (!string.IsNullOrWhiteSpace(linkTokenValue))
        {
            rows += await connection.ExecuteAsync(
                """
                UPDATE accounts
                SET link_token = NULL, link_token_expires_at = NULL, updated_at = @now
                WHERE link_token = @link_token
                """,
                new { link_token = linkTokenValue, now = nowMs });
        }

        if (!string.IsNullOrWhiteSpace(systemKey))
        {
            rows += await connection.ExecuteAsync(
                """
                UPDATE accounts
                SET link_token = NULL, link_token_expires_at = NULL, updated_at = @now
                WHERE system_id = @system_id
                """,
                new { system_id = systemKey, now = nowMs });
        }

        return rows;
    }

    private async Task<AccountRow?> LoadAccountRowAsync(SystemId systemId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.QueryFirstOrDefaultAsync<AccountRow>(
            """
            SELECT username AS Username, description AS Description, avatar_url AS AvatarUrl,
                   avatar_source AS AvatarSource, discord_id AS DiscordId, email AS Email, apple_id AS AppleId
            FROM accounts
            WHERE system_id = @system_id
            LIMIT 1
            """,
            new { system_id = systemId });
    }

    private sealed record AccountRow(
        string? Username,
        string? Description,
        string? AvatarUrl,
        long? AvatarSource,
        string? DiscordId,
        string? Email,
        string? AppleId);

    private sealed record LinkTokenRow(string? LinkToken, long ExpiresAt);

    private sealed record LinkTokenOwnerRow(SystemId SystemId, long ExpiresAt);
}
