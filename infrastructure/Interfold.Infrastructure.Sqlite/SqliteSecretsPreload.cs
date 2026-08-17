using Microsoft.Data.Sqlite;

namespace Interfold.Infrastructure.Sqlite;

/// <summary>Bare-connection secrets read for <c>SecretsPreBuildLoader</c> before DI is built.</summary>
public static class SqliteSecretsPreload
{
    public static Dictionary<string, string?> Fetch(string connectionString, IReadOnlyList<string> keys)
    {
        var rows = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            // Build IN clause — SQLite has no ANY(@array) like Postgres.
            var paramNames = new string[keys.Count];
            for (var i = 0; i < keys.Count; i++)
            {
                paramNames[i] = $"$k{i}";
                cmd.Parameters.AddWithValue(paramNames[i], keys[i]);
            }

            cmd.CommandText = $"SELECT key, value FROM secrets WHERE key IN ({string.Join(", ", paramNames)})";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to fetch startup secrets from SQLite secrets table. Ensure the database " +
                "file exists, migrations have run, and required rows are seeded.", ex);
        }

        return rows;
    }
}
