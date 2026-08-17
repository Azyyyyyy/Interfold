using Interfold.Shared.Contracts.Secrets;

namespace Interfold.Infrastructure.Sqlite;

public sealed class SqliteSecretsStore(ISqliteConnectionFactory connectionFactory) : ISecretsStore
{
    public async Task<string?> GetAsync(SecretsStoreKey key, CancellationToken cancellationToken = default)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM secrets WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key.Value);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    public async Task<string> GetRequiredAsync(SecretsStoreKey key, CancellationToken cancellationToken = default)
    {
        var value = await GetAsync(key, cancellationToken);
        return value ?? throw new InvalidOperationException($"Required secret '{key.Value}' not found in secrets store.");
    }

    public async Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT key, value, created_by, created_at, updated_at, expires_at, rotated_from FROM secrets ORDER BY key";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var results = new List<SecretEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SecretEntry(
                Key: new SecretsStoreKey(reader.GetString(0)),
                Value: reader.GetString(1),
                CreatedBy: reader.GetString(2),
                CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                ExpiresAt: reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                RotatedFrom: reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return results;
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
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO secrets (key, value, created_by, created_at, updated_at, expires_at, rotated_from)
            VALUES ($key, $value, $created_by, $created_at, $updated_at, NULL, NULL)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$key", key.Value);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$created_by", createdBy);
        cmd.Parameters.AddWithValue("$created_at", nowMs);
        cmd.Parameters.AddWithValue("$updated_at", nowMs);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
