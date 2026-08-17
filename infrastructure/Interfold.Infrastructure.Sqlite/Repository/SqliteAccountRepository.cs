using System.Security.Cryptography;
using System.Text;
using Interfold.Auth.Contracts.Ids;
using Interfold.Settings.Contracts.Ids;
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
    private static readonly TimeSpan LinkTokenTtl = TimeSpan.FromMinutes(5);

    private enum ProviderColumn
    {
        Discord,
        Email,
        Apple,
    }

    private static string ColumnName(ProviderColumn column) => column switch
    {
        ProviderColumn.Discord => "discord_id",
        ProviderColumn.Email => "email",
        ProviderColumn.Apple => "apple_id",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unknown provider column"),
    };

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IRegionContext _regionContext;
    private readonly IEncryptionStateRepository? _encryptionStates;
    private readonly TimeProvider _timeProvider;

    public SqliteAccountRepository(
        ISqliteConnectionFactory connectionFactory,
        IRegionContext regionContext,
        IEncryptionStateRepository? encryptionStates = null,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _regionContext = regionContext;
        _encryptionStates = encryptionStates;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> UpdateUsernameAsync(SystemId systemId, Username username, CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        await UpsertProfileFieldAsync(systemKey, "username", username.Value, cancellationToken);
        return true;
    }

    public async Task<bool> UpdateDescriptionAsync(SystemId systemId, string description, CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        await UpsertProfileFieldAsync(systemKey, "description", description, cancellationToken);
        return true;
    }

    public async Task<bool> UpdateAvatarAsync(
        SystemId systemId,
        AvatarUrl avatarUrl,
        AvatarSource source,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts (system_id, avatar_url, avatar_source, created_at, updated_at)
            VALUES ($system_id, $avatar_url, $avatar_source, $now, $now)
            ON CONFLICT(system_id) DO UPDATE SET
                avatar_url = excluded.avatar_url,
                avatar_source = excluded.avatar_source,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$avatar_url", avatarUrl.Value);
        command.Parameters.AddWithValue("$avatar_source", source.ToWire());
        command.Parameters.AddWithValue("$now", nowMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ClearAvatarAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE accounts
            SET avatar_url = NULL, avatar_source = NULL, updated_at = $now
            WHERE system_id = $system_id
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$now", nowMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<LinkToken> GetOrCreateLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var scoped = ResolveScoped(systemId);
        var systemKey = scoped.Value;
        var now = _timeProvider.GetUtcNow();
        var expiresAt = now.Add(LinkTokenTtl).ToUnixTimeMilliseconds();
        var nowMs = now.ToUnixTimeMilliseconds();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(systemKey));
        var tokenValue = Convert.ToHexString(hash)[..32].ToLowerInvariant();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts (system_id, link_token, link_token_expires_at, created_at, updated_at)
            VALUES ($system_id, $link_token, $expires_at, $now, $now)
            ON CONFLICT(system_id) DO UPDATE SET
                link_token = excluded.link_token,
                link_token_expires_at = excluded.link_token_expires_at,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$link_token", tokenValue);
        command.Parameters.AddWithValue("$expires_at", expiresAt);
        command.Parameters.AddWithValue("$now", nowMs);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new LinkToken(tokenValue);
    }

    public async Task<LinkToken?> GetLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT link_token, link_token_expires_at
            FROM accounts
            WHERE system_id = $system_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        var token = reader.GetString(0);
        var expiresAt = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
        if (expiresAt > nowMs)
        {
            return new LinkToken(token);
        }

        await reader.DisposeAsync();
        await ScrubLinkTokenAsync(connection, systemKey: systemKey, cancellationToken: cancellationToken);
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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT system_id, link_token_expires_at
            FROM accounts
            WHERE link_token = $link_token
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$link_token", linkToken.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var systemKey = reader.GetString(0);
        var expiresAt = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
        if (expiresAt > nowMs)
        {
            return new SystemId(systemKey);
        }

        await reader.DisposeAsync();
        await ScrubLinkTokenAsync(connection, linkTokenValue: linkToken.Value, cancellationToken: cancellationToken);
        return null;
    }

    public async Task<bool> ClearLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await ScrubLinkTokenAsync(connection, systemKey: systemKey, cancellationToken: cancellationToken);
        return true;
    }

    public Task<SystemId?> TryFindSystemIdByDiscordIdAsync(DiscordId discordId, CancellationToken cancellationToken = default)
        => TryFindByProviderAsync(ProviderColumn.Discord, discordId.Value, cancellationToken);

    public Task<SystemId?> FindOrCreateSystemIdAsync(ProviderIdentity identity, CancellationToken cancellationToken = default)
        => identity.MatchOrThrow(
            discordId => FindOrCreateByProviderAsync(ProviderColumn.Discord, discordId.Value, cancellationToken),
            email => FindOrCreateByProviderAsync(ProviderColumn.Email, email.Value, cancellationToken),
            appleId => FindOrCreateByProviderAsync(ProviderColumn.Apple, appleId.Value, cancellationToken));

    public Task<AccountLinkResult> LinkIdentityToUserAsync(
        SystemId systemId,
        ProviderIdentity identity,
        CancellationToken cancellationToken = default)
        => identity.MatchOrThrow(
            discordId => LinkProviderAsync(systemId, ProviderColumn.Discord, discordId.Value, cancellationToken),
            email => LinkProviderAsync(systemId, ProviderColumn.Email, email.Value, cancellationToken),
            appleId => LinkProviderAsync(systemId, ProviderColumn.Apple, appleId.Value, cancellationToken));

    public Task<bool> UnlinkDiscordAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => UnlinkProviderAsync(systemId, ProviderColumn.Discord, cancellationToken);

    public Task<bool> UnlinkEmailAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => UnlinkProviderAsync(systemId, ProviderColumn.Email, cancellationToken);

    public Task<bool> UnlinkAppleAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => UnlinkProviderAsync(systemId, ProviderColumn.Apple, cancellationToken);

    public async Task<bool> DeleteAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM accounts WHERE system_id = $system_id";
        command.Parameters.AddWithValue("$system_id", systemKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
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
            ParseAvatarSource(row.AvatarSource),
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
            AvatarSource: ParseAvatarSource(row.AvatarSource),
            Username: row.Username is { } u ? new Username(u) : null,
            Description: row.Description);
    }

    private ScopedSystemId ResolveScoped(SystemId systemId)
        => ScopedSystemId.Compose(_regionContext.ResolveUserRegion(systemId), systemId);

    private async Task UpsertProfileFieldAsync(
        string systemKey,
        string column,
        string value,
        CancellationToken cancellationToken)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO accounts (system_id, {column}, created_at, updated_at)
            VALUES ($system_id, $value, $now, $now)
            ON CONFLICT(system_id) DO UPDATE SET
                {column} = excluded.{column},
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$now", nowMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SystemId?> TryFindByProviderAsync(
        ProviderColumn column,
        string value,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var columnName = ColumnName(column);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT system_id
            FROM accounts
            WHERE {columnName} = $value COLLATE NOCASE
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$value", value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string systemKey ? new SystemId(systemKey) : null;
    }

    private async Task<SystemId?> FindOrCreateByProviderAsync(
        ProviderColumn column,
        string value,
        CancellationToken cancellationToken)
    {
        var existing = await TryFindByProviderAsync(column, value, cancellationToken);
        if (existing is { } typed && !string.IsNullOrWhiteSpace(typed.Value))
        {
            return typed;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNew = ScopedSystemId.Compose(_regionContext.ResolveUserRegion(new(newSystemId)), newSystemId);
        var columnName = ColumnName(column);
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO accounts (system_id, {columnName}, created_at, updated_at)
            VALUES ($system_id, $value, $now, $now)
            """;
        command.Parameters.AddWithValue("$system_id", scopedNew.Value);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$now", nowMs);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException)
        {
            // Concurrent create won the unique index — re-read the winner.
            return await TryFindByProviderAsync(column, value, cancellationToken);
        }

        await EnsureEncryptionSaltForSystemAsync(scopedNew, cancellationToken);
        return scopedNew.AsSystemId();
    }

    private async Task<AccountLinkResult> LinkProviderAsync(
        SystemId systemId,
        ProviderColumn column,
        string value,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AccountLinkResult.UserNotFound;
        }

        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var scopedSystemId = ResolveScoped(systemId);
        var columnName = ColumnName(column);
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT username, description, avatar_url, link_token, discord_id, email, apple_id
            FROM accounts
            WHERE system_id = $system_id
            LIMIT 1
            """;
        select.Parameters.AddWithValue("$system_id", systemKey);

        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return AccountLinkResult.UserNotFound;
        }

        var username = reader.IsDBNull(0) ? null : reader.GetString(0);
        var description = reader.IsDBNull(1) ? null : reader.GetString(1);
        var avatarUrl = reader.IsDBNull(2) ? null : reader.GetString(2);
        var linkToken = reader.IsDBNull(3) ? null : reader.GetString(3);
        var discordId = reader.IsDBNull(4) ? null : reader.GetString(4);
        var email = reader.IsDBNull(5) ? null : reader.GetString(5);
        var appleId = reader.IsDBNull(6) ? null : reader.GetString(6);

        if (username is null && description is null && avatarUrl is null && linkToken is null)
        {
            return AccountLinkResult.UserNotFound;
        }

        var existingLinked = column switch
        {
            ProviderColumn.Discord => discordId,
            ProviderColumn.Email => email,
            ProviderColumn.Apple => appleId,
            _ => null,
        };

        if (!string.IsNullOrWhiteSpace(existingLinked))
        {
            return AccountLinkResult.AlreadyLinked;
        }

        await reader.DisposeAsync();

        var owner = await TryFindByProviderAsync(column, value, cancellationToken);
        if (owner is { } typedOwner
            && !string.IsNullOrWhiteSpace(typedOwner.Value)
            && !string.Equals(typedOwner.Value, scopedSystemId.Value, StringComparison.Ordinal))
        {
            return AccountLinkResult.UserExists;
        }

        await using var update = connection.CreateCommand();
        update.CommandText = $"""
            UPDATE accounts
            SET {columnName} = $value, updated_at = $now
            WHERE system_id = $system_id
            """;
        update.Parameters.AddWithValue("$value", value);
        update.Parameters.AddWithValue("$now", nowMs);
        update.Parameters.AddWithValue("$system_id", systemKey);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return AccountLinkResult.Success;
    }

    private async Task<bool> UnlinkProviderAsync(
        SystemId systemId,
        ProviderColumn column,
        CancellationToken cancellationToken)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var columnName = ColumnName(column);
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE accounts
            SET {columnName} = NULL, updated_at = $now
            WHERE system_id = $system_id
            """;
        command.Parameters.AddWithValue("$now", nowMs);
        command.Parameters.AddWithValue("$system_id", systemKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private async Task EnsureEncryptionSaltForSystemAsync(ScopedSystemId scoped, CancellationToken cancellationToken)
    {
        if (_encryptionStates is null)
            return;

        await _encryptionStates.UpsertAsync(
            scoped.AsSystemId(),
            false,
            null,
            EncryptionSalt.NewRandom(),
            cancellationToken);
    }

    private static async Task ScrubLinkTokenAsync(
        SqliteConnection connection,
        string? systemKey = null,
        string? linkTokenValue = null,
        CancellationToken cancellationToken = default)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (!string.IsNullOrWhiteSpace(linkTokenValue))
        {
            await using var byToken = connection.CreateCommand();
            byToken.CommandText = """
                UPDATE accounts
                SET link_token = NULL, link_token_expires_at = NULL, updated_at = $now
                WHERE link_token = $link_token
                """;
            byToken.Parameters.AddWithValue("$link_token", linkTokenValue);
            byToken.Parameters.AddWithValue("$now", nowMs);
            await byToken.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(systemKey))
        {
            await using var bySystem = connection.CreateCommand();
            bySystem.CommandText = """
                UPDATE accounts
                SET link_token = NULL, link_token_expires_at = NULL, updated_at = $now
                WHERE system_id = $system_id
                """;
            bySystem.Parameters.AddWithValue("$system_id", systemKey);
            bySystem.Parameters.AddWithValue("$now", nowMs);
            await bySystem.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<AccountRow?> LoadAccountRowAsync(SystemId systemId, CancellationToken cancellationToken)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT username, description, avatar_url, avatar_source, discord_id, email, apple_id
            FROM accounts
            WHERE system_id = $system_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AccountRow(
            Username: reader.IsDBNull(0) ? null : reader.GetString(0),
            Description: reader.IsDBNull(1) ? null : reader.GetString(1),
            AvatarUrl: reader.IsDBNull(2) ? null : reader.GetString(2),
            AvatarSource: reader.IsDBNull(3) ? null : reader.GetString(3),
            DiscordId: reader.IsDBNull(4) ? null : reader.GetString(4),
            Email: reader.IsDBNull(5) ? null : reader.GetString(5),
            AppleId: reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static AvatarSource? ParseAvatarSource(string? wire)
        => wire is not null && wire.TryParseWire(out AvatarSource source) ? source : null;

    private sealed record AccountRow(
        string? Username,
        string? Description,
        string? AvatarUrl,
        string? AvatarSource,
        string? DiscordId,
        string? Email,
        string? AppleId);
}
