using Npgsql;

namespace Interfold.StackMove;

public sealed record CqlEndpoint(
    string PostgresConnectionString,
    string ContactPoints,
    int Port,
    string? Username,
    string? Password,
    string Keyspace,
    string LocalDatacenter);

public sealed class MoveRequest
{
    public required StackKind From { get; init; }
    public required StackKind To { get; init; }
    public bool DryRun { get; init; }
    public bool AllowLossy { get; init; }
    public string? SourceSqlitePath { get; init; }
    public string? TargetSqlitePath { get; init; }
    public CqlEndpoint? SourceCql { get; init; }
    public CqlEndpoint? TargetCql { get; init; }
    internal BootstrapPeek? Bootstrap { get; init; }
    internal string? SecretsPath { get; init; }
}

internal static class BootstrapReport
{
    public static string Render(MoveRequest request, MoveCounts counts, LossyCounts lossy, bool wrote)
    {
        var lines = new List<string>();
        if (request.DryRun)
        {
            lines.Add("Dry run. Nothing was written.");
        }
        else if (wrote)
        {
            lines.Add("Move complete.");
        }
        else
        {
            lines.Add("Move did not write.");
        }

        lines.Add($"  accounts: {counts.Accounts}");
        lines.Add($"  alters: {counts.Alters}");
        lines.Add($"  tags: {counts.Tags}");
        lines.Add($"  friendships: {counts.Friendships}");
        lines.Add($"  friend requests: {counts.FriendRequests}");
        lines.Add($"  notification tokens: {counts.NotificationTokens}");
        lines.Add($"  fronts: {counts.Fronts}");
        lines.Add($"  journals: {counts.Journals}");
        lines.Add($"  polls: {counts.Polls}");
        lines.Add($"  secrets copied: {counts.Secrets}");

        if (lossy.Any)
        {
            lines.Add("Values the target cannot store:");
            if (lossy.GoogleIds > 0)
            {
                lines.Add($"  google_id: {lossy.GoogleIds}");
            }

            if (lossy.ExtraImages > 0)
            {
                lines.Add($"  extra_images: {lossy.ExtraImages}");
            }

            if (lossy.DiscordProxies > 0)
            {
                lines.Add($"  discord_proxies: {lossy.DiscordProxies}");
            }

            if (lossy.DiscordSettings > 0)
            {
                lines.Add($"  discord_settings: {lossy.DiscordSettings}");
            }

            if (lossy.LinkTokens > 0)
            {
                lines.Add($"  link_token: {lossy.LinkTokens} (Scylla and Cassandra keep link tokens in process memory)");
            }
        }

        var avatar = request.Bootstrap?.AvatarStorageRoot;
        lines.Add(string.IsNullOrWhiteSpace(avatar)
            ? "Avatars were not copied. api.storage.avatarStorageRoot is unchanged."
            : $"Avatars were not copied. They stay at {avatar}.");
        lines.Add("auth_tokens and idempotency were not copied. Users sign in again.");
        lines.Add(counts.Secrets > 0
            ? "The encryption pepper and JWT signing keys were copied, so existing ciphertext still opens."
            : "No encryption pepper was found on the source. Confirm ciphertext before cutting the API over.");

        if (!string.IsNullOrWhiteSpace(request.SecretsPath))
        {
            lines.Add($"{request.SecretsPath} was not modified.");
        }

        lines.Add("interfold.bootstrap.json was not modified. Set:");
        lines.Add(Snippet(request));

        if (request.To == StackKind.Sqlite)
        {
            lines.Add("datastores.postgres and datastores.cql can stay in the file. They are unused while persistence is sqlite.");
            lines.Add("Stop the API, then run bootstrap. Init migrates the file this tool wrote and skips secret reseed because the pepper is already present.");
        }
        else
        {
            lines.Add(request.DryRun || !wrote
                ? "Bring up an empty target stack before the move so this tool has somewhere to write."
                : "The empty target stack already received the rows.");
            lines.Add("A later bootstrap is idempotent and must not reseed a new encryption pepper.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string Snippet(MoveRequest request)
    {
        if (request.To == StackKind.Sqlite)
        {
            return """
                  "datastores": {
                    "persistence": "sqlite"
                  }
                """;
        }

        var database = PostgresDatabase(request);
        var keyspace = request.TargetCql?.Keyspace ?? request.Bootstrap?.Keyspace ?? "nam";
        var backend = StackKindParser.CqlBackendWire(request.To);
        var clusterLine = string.IsNullOrWhiteSpace(request.Bootstrap?.ClusterName)
            ? ""
            : $"\n      \"clusterName\": \"{request.Bootstrap.ClusterName}\",";

        return "  \"datastores\": {\n" +
               "    \"persistence\": \"scylla-postgres\",\n" +
               $"    \"postgres\": {{ \"database\": \"{database}\" }},\n" +
               "    \"cql\": {" + clusterLine + "\n" +
               $"      \"backend\": \"{backend}\",\n" +
               $"      \"keyspace\": \"{keyspace}\"\n" +
               "    }\n" +
               "  }";
    }

    private static string PostgresDatabase(MoveRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Bootstrap?.PostgresDatabase))
        {
            return request.Bootstrap.PostgresDatabase;
        }

        var connection = request.TargetCql?.PostgresConnectionString ?? request.SourceCql?.PostgresConnectionString;
        if (string.IsNullOrWhiteSpace(connection))
        {
            return "interfold";
        }

        var builder = new NpgsqlConnectionStringBuilder(connection);
        return string.IsNullOrWhiteSpace(builder.Database) ? "interfold" : builder.Database;
    }
}
