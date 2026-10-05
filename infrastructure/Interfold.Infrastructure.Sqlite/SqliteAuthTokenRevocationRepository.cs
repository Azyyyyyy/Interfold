using Dapper;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.Sqlite;

/// <summary>SQLite-backed <see cref="IAuthTokenRevocationRepository"/> — one row per
/// issued JWT keyed on JTI.</summary>
public sealed class SqliteAuthTokenRevocationRepository : IAuthTokenRevocationRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public SqliteAuthTokenRevocationRepository(ISqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public async Task RecordTokenAsync(
        Jti jti,
        SystemId systemId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId.Value, nameof(systemId));

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO auth_tokens (jti, system_id, issued_at, expires_at, revoked_at)
            VALUES (@jti, @system_id, @issued_at, @expires_at, NULL)
            ON CONFLICT(jti) DO NOTHING
            """,
            new
            {
                jti = jti.Value,
                system_id = systemId.Value,
                issued_at = nowMs,
                expires_at = expiresAt.ToUnixTimeMilliseconds(),
            });
    }

    public async Task<bool> ValidateTokenNotRevokedAsync(
        Jti jti,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var found = await connection.QueryFirstOrDefaultAsync<long?>(
            """
            SELECT 1
            FROM auth_tokens
            WHERE jti = @jti
              AND revoked_at IS NULL
              AND expires_at > @now
            LIMIT 1
            """,
            new { jti = jti.Value, now = nowMs });
        return found is not null;
    }

    public async Task RevokeTokenAsync(
        Jti jti,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            UPDATE auth_tokens
            SET revoked_at = @revoked_at
            WHERE jti = @jti
              AND revoked_at IS NULL
            """,
            new { jti = jti.Value, revoked_at = nowMs });
    }
}
