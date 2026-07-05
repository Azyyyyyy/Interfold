using Interfold.Contracts.Configuration;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Npgsql;
using Interfold.Contracts.Ids;

namespace Interfold.Infrastructure.Postgres;

/// <summary>
/// Postgres-backed implementation of JWT token revocation tracking.
/// Stores issued tokens by JTI (JWT ID) to enable per-token revocation without refresh tokens.
/// </summary>
public sealed class AuthTokenRevocationRepository : IAuthTokenRevocationRepository
{
    private readonly IPostgresConnectionFactory _connectionFactory;
    private readonly PersistenceConfiguration _options;

    public AuthTokenRevocationRepository(
        IPostgresConnectionFactory connectionFactory,
        PersistenceConfiguration options)
    {
        _connectionFactory = connectionFactory;
        _options = options;
    }

    public async Task RecordTokenAsync(
        Jti jti,
        SystemId systemId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId.Value, nameof(systemId));

        await DatabaseTransientRetry.ExecutePostgresAsync(async () =>
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(@"
                INSERT INTO auth_tokens (jti, system_id, issued_at, expires_at, revoked_at)
                VALUES (@jti, @system_id, NOW(), @expires_at, NULL)
                ON CONFLICT (jti) DO NOTHING", connection);

            command.Parameters.AddWithValue("jti", jti.Value);
            command.Parameters.AddWithValue("system_id", systemId.Value);
            command.Parameters.AddWithValue("expires_at", expiresAt.UtcDateTime);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }, _options, cancellationToken);
    }

    public async Task<bool> ValidateTokenNotRevokedAsync(
        Jti jti,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));

        return await DatabaseTransientRetry.ExecutePostgresAsync(async () =>
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(@"
                SELECT 1
                FROM auth_tokens
                WHERE jti = @jti
                  AND revoked_at IS NULL
                  AND expires_at > NOW()
                LIMIT 1", connection);

            command.Parameters.AddWithValue("jti", jti.Value);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken);
        }, _options, cancellationToken);
    }

    public async Task RevokeTokenAsync(
        Jti jti,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));

        await DatabaseTransientRetry.ExecutePostgresAsync(async () =>
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(@"
                UPDATE auth_tokens
                SET revoked_at = NOW()
                WHERE jti = @jti
                  AND revoked_at IS NULL", connection);

            command.Parameters.AddWithValue("jti", jti.Value);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }, _options, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> FindTokensBySystemIdAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId.Value, nameof(systemId));

        return await DatabaseTransientRetry.ExecutePostgresAsync(async () =>
        {
            var tokens = new List<string>();

            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(@"
                SELECT jti
                FROM auth_tokens
                WHERE system_id = @system_id
                  AND revoked_at IS NULL
                  AND expires_at > NOW()
                ORDER BY issued_at DESC", connection);

            command.Parameters.AddWithValue("system_id", systemId.Value);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tokens.Add(reader.GetString(0));
            }

            return tokens.AsReadOnly();
        }, _options, cancellationToken);
    }

    public async Task<int> CleanupExpiredTokensAsync(
        DateTimeOffset? olderThan = null,
        CancellationToken cancellationToken = default)
    {
        var cleanupBefore = (olderThan ?? DateTimeOffset.UtcNow).UtcDateTime;

        return await DatabaseTransientRetry.ExecutePostgresAsync(async () =>
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(@"
                DELETE FROM auth_tokens
                WHERE revoked_at IS NOT NULL
                   OR expires_at < @cleanup_before
                LIMIT 5000", connection);

            command.Parameters.AddWithValue("cleanup_before", cleanupBefore);

            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, _options, cancellationToken);
    }
}
