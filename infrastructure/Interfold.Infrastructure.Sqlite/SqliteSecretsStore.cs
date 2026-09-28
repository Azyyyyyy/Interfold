using Dapper;
using Interfold.Shared.Contracts.Secrets;

namespace Interfold.Infrastructure.Sqlite;

public sealed class SqliteSecretsStore(ISqliteConnectionFactory connectionFactory) : ISecretsStore
{
    public async Task<string?> GetAsync(SecretsStoreKey key, CancellationToken cancellationToken = default)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM secrets WHERE key = @key",
            new { key = key.Value });
    }

    public async Task<string> GetRequiredAsync(SecretsStoreKey key, CancellationToken cancellationToken = default)
    {
        var value = await GetAsync(key, cancellationToken);
        return value ?? throw new InvalidOperationException($"Required secret '{key.Value}' not found in secrets store.");
    }

    public async Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<SecretRow>(
            """
            SELECT key AS Key, value AS Value, created_by AS CreatedBy, created_at AS CreatedAt,
                   updated_at AS UpdatedAt, expires_at AS ExpiresAt, rotated_from AS RotatedFrom
            FROM secrets
            ORDER BY key
            """);
        return rows.Select(row => new SecretEntry(
            Key: new SecretsStoreKey(row.Key),
            Value: row.Value,
            CreatedBy: row.CreatedBy,
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAt),
            UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAt),
            ExpiresAt: row.ExpiresAt is { } exp ? DateTimeOffset.FromUnixTimeMilliseconds(exp) : null,
            RotatedFrom: row.RotatedFrom)).ToList();
    }

    /// <summary>Upsert used by fixtures / bootstrap — not part of <see cref="ISecretsStore"/>.</summary>
    public async Task UpsertAsync(
        SecretsStoreKey key,
        string value,
        string createdBy = "bootstrap",
        CancellationToken cancellationToken = default)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var conn = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await conn.ExecuteAsync(
            """
            INSERT INTO secrets (key, value, created_by, created_at, updated_at, expires_at, rotated_from)
            VALUES (@key, @value, @created_by, @created_at, @updated_at, NULL, NULL)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at = excluded.updated_at
            """,
            new
            {
                key = key.Value,
                value,
                created_by = createdBy,
                created_at = nowMs,
                updated_at = nowMs,
            });
    }

    private sealed record SecretRow(
        string Key,
        string Value,
        string CreatedBy,
        long CreatedAt,
        long UpdatedAt,
        long? ExpiresAt,
        string? RotatedFrom);
}
