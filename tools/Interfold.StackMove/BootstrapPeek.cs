using System.Text.Json;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.StackMove;

internal sealed class BootstrapPeek
{
    public string? OutputDir { get; init; }
    public string? Persistence { get; init; }
    public string? PostgresDatabase { get; init; }
    public string? CqlBackend { get; init; }
    public string? ClusterName { get; init; }
    public string? Keyspace { get; init; }
    public string? AvatarStorageRoot { get; init; }

    public string? SqlitePath =>
        string.IsNullOrWhiteSpace(OutputDir)
            ? null
            : Path.GetFullPath(Path.Combine(
                OutputDir,
                "data",
                "sqlite",
                ContainerMountPaths.InterfoldSqliteDbFileName));

    public static BootstrapPeek Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        string? outputDir = null;
        if (root.TryGetProperty("deployment", out var deployment) &&
            deployment.TryGetProperty("outputDir", out var outputDirElement))
        {
            outputDir = outputDirElement.GetString();
        }

        string? persistence = null;
        string? postgresDatabase = null;
        string? cqlBackend = null;
        string? clusterName = null;
        string? keyspace = null;
        if (root.TryGetProperty("datastores", out var datastores))
        {
            if (datastores.TryGetProperty("persistence", out var persistenceElement))
            {
                persistence = persistenceElement.GetString();
            }

            if (datastores.TryGetProperty("postgres", out var postgres) &&
                postgres.TryGetProperty("database", out var databaseElement))
            {
                postgresDatabase = databaseElement.GetString();
            }

            if (datastores.TryGetProperty("cql", out var cql))
            {
                if (cql.TryGetProperty("backend", out var backendElement))
                {
                    cqlBackend = backendElement.GetString();
                }

                if (cql.TryGetProperty("clusterName", out var clusterElement))
                {
                    clusterName = clusterElement.GetString();
                }

                if (cql.TryGetProperty("keyspace", out var keyspaceElement))
                {
                    keyspace = keyspaceElement.GetString();
                }
            }
        }

        string? avatarRoot = null;
        if (root.TryGetProperty("api", out var api) &&
            api.TryGetProperty("storage", out var storage) &&
            storage.TryGetProperty("avatarStorageRoot", out var avatarElement))
        {
            avatarRoot = avatarElement.GetString();
        }

        return new BootstrapPeek
        {
            OutputDir = outputDir,
            Persistence = persistence,
            PostgresDatabase = postgresDatabase,
            CqlBackend = cqlBackend,
            ClusterName = clusterName,
            Keyspace = keyspace,
            AvatarStorageRoot = string.IsNullOrWhiteSpace(avatarRoot) ? null : avatarRoot,
        };
    }
}
