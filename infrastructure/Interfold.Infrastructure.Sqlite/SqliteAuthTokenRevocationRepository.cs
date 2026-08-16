using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.Sqlite;

/// <summary>SQLite-backed <see cref="IAuthTokenRevocationRepository"/> — one row per
/// issued JWT keyed on JTI.</summary>
public sealed class SqliteAuthTokenRevocationRepository(ISqliteConnectionFactory connectionFactory)
    : IAuthTokenRevocationRepository
{
    public async Task RecordTokenAsync(
        Jti jti,
        SystemId systemId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId.Value, nameof(systemId));

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO auth_tokens (jti, system_id, issued_at, expires_at, revoked_at)
            VALUES ($jti, $system_id, $issued_at, $expires_at, NULL)
            ON CONFLICT(jti) DO NOTHING
            """;
        command.Parameters.AddWithValue("$jti", jti.Value);
        command.Parameters.AddWithValue("$system_id", systemId.Value);
        command.Parameters.AddWithValue("$issued_at", nowMs);
        command.Parameters.AddWithValue("$expires_at", expiresAt.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ValidateTokenNotRevokedAsync(
        Jti jti,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM auth_tokens
            WHERE jti = $jti
              AND revoked_at IS NULL
              AND expires_at > $now
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$jti", jti.Value);
        command.Parameters.AddWithValue("$now", nowMs);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    public async Task RevokeTokenAsync(
        Jti jti,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE auth_tokens
            SET revoked_at = $revoked_at
            WHERE jti = $jti
              AND revoked_at IS NULL
            """;
        command.Parameters.AddWithValue("$jti", jti.Value);
        command.Parameters.AddWithValue("$revoked_at", nowMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
