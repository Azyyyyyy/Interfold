namespace Interfold.Shared.Contracts.Configuration;

/// <summary>
/// Docker Compose network names the AppHost graph emits. Operator-frozen alongside
/// <see cref="ComposeServices"/>: existing deployments reference them (e.g.
/// <c>docker network inspect</c> runbooks), and renaming would orphan the old networks
/// on update.
/// </summary>
public static class ComposeNetworks
{
    public const string EdgeApi = "edge-api";
    public const string EdgeWeb = "edge-web";
}

/// <summary>
/// Docker Compose named volumes the AppHost graph emits. Operator-frozen: renaming a
/// volume silently detaches existing data on the next <c>docker compose up</c>.
/// </summary>
public static class ComposeVolumes
{
    public const string InterfoldSqliteData = "interfold_sqlite_data";
}
