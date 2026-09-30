using System.Text.Json;
using Npgsql;

namespace Interfold.StackMove;

internal sealed class DeploymentSecrets
{
    public required string FilePath { get; init; }
    public string PostgresUser { get; init; } = "interfold";
    public string PostgresAdminPassword { get; init; } = string.Empty;
    public string ScyllaUser { get; init; } = "interfold";
    public string ScyllaPassword { get; init; } = string.Empty;

    public static DeploymentSecrets? LoadOptional(string? explicitPath, string? outputDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!File.Exists(explicitPath))
            {
                throw new MoveRefusedException($"Secrets file not found: {explicitPath}");
            }

            return Load(explicitPath);
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return null;
        }

        var discovered = Path.GetFullPath(Path.Combine(outputDir, "secrets", "secrets.json"));
        return File.Exists(discovered) ? Load(discovered) : null;
    }

    // The app role only has SELECT on internal.secrets. The move writes that table.
    public string PostgresAdminConnectionString(string? database)
    {
        if (string.IsNullOrWhiteSpace(PostgresAdminPassword))
        {
            throw new MoveRefusedException($"{FilePath} has no postgresAdminPassword.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 5432,
            Database = string.IsNullOrWhiteSpace(database) ? "interfold" : database,
            Username = $"{PostgresUser}_admin",
            Password = PostgresAdminPassword,
        };
        return builder.ConnectionString;
    }

    private static DeploymentSecrets Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        return new DeploymentSecrets
        {
            FilePath = path,
            PostgresUser = Text(root, "postgresUser") ?? "interfold",
            PostgresAdminPassword = Text(root, "postgresAdminPassword") ?? string.Empty,
            ScyllaUser = Text(root, "scyllaUser") ?? "interfold",
            ScyllaPassword = Text(root, "scyllaPassword") ?? string.Empty,
        };
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
