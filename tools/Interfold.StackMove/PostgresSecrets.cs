using Npgsql;

namespace Interfold.StackMove;

internal static class PostgresSecrets
{
    public static async Task<List<SecretRow>> ReadAsync(string connectionString, CancellationToken cancellationToken)
    {
        var rows = new List<SecretRow>();
        await using var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException exception)
        {
            throw new MoveRefusedException(
                $"Could not open Postgres ({SafeTarget(connectionString)}). {exception.Message}");
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT key, value, created_by, created_at, updated_at, expires_at, rotated_from
            FROM internal.secrets
            """,
            connection);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new SecretRow
                {
                    Key = reader.GetString(0),
                    Value = reader.GetString(1),
                    CreatedBy = reader.GetString(2),
                    CreatedAtUnixMs = ToUnix(reader, 3),
                    UpdatedAtUnixMs = ToUnix(reader, 4),
                    ExpiresAtUnixMs = reader.IsDBNull(5) ? null : ToUnix(reader, 5),
                    RotatedFrom = reader.IsDBNull(6) ? null : reader.GetString(6),
                });
            }
        }
        catch (PostgresException exception) when (exception.SqlState == "42P01")
        {
            throw new MoveRefusedException(
                "Postgres has no internal.secrets table. Bring the target stack up so migrations have run.");
        }

        return rows;
    }

    private static long ToUnix(NpgsqlDataReader reader, int ordinal)
    {
        var value = reader.GetFieldValue<DateTime>(ordinal);
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }

    public static async Task UpsertAsync(
        string connectionString,
        IReadOnlyList<SecretRow> secrets,
        CancellationToken cancellationToken)
    {
        if (secrets.Count == 0)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var secret in secrets)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO internal.secrets (key, value, created_by, created_at, updated_at, expires_at, rotated_from)
                VALUES (@key, @value, @created_by, @created_at, @updated_at, @expires_at, @rotated_from)
                ON CONFLICT (key) DO UPDATE SET
                    value = EXCLUDED.value,
                    created_by = EXCLUDED.created_by,
                    updated_at = EXCLUDED.updated_at,
                    expires_at = EXCLUDED.expires_at,
                    rotated_from = EXCLUDED.rotated_from
                """,
                connection);
            command.Parameters.AddWithValue("key", secret.Key);
            command.Parameters.AddWithValue("value", secret.Value);
            command.Parameters.AddWithValue("created_by", secret.CreatedBy);
            command.Parameters.AddWithValue("created_at", UnixMs.ToOffset(secret.CreatedAtUnixMs));
            command.Parameters.AddWithValue("updated_at", UnixMs.ToOffset(secret.UpdatedAtUnixMs));
            command.Parameters.AddWithValue("expires_at", (object?)UnixMs.ToOffset(secret.ExpiresAtUnixMs) ?? DBNull.Value);
            command.Parameters.AddWithValue("rotated_from", (object?)secret.RotatedFrom ?? DBNull.Value);
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState == "42501")
            {
                throw new MoveRefusedException(
                    "The Postgres role cannot write internal.secrets. Pass the admin connection string; the app role only has SELECT.");
            }
        }
    }

    private static string SafeTarget(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"{builder.Host}:{builder.Port}/{builder.Database}";
        }
        catch (ArgumentException)
        {
            return "the postgres connection string";
        }
    }
}
