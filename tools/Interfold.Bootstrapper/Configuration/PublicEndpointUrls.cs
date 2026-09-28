using Interfold.Bootstrapper.Cli;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>Operator-facing public API / web URLs derived from a resolved bootstrap config.</summary>
internal static class PublicEndpointUrls
{
    private const int DefaultHttpsPort = 443;

    internal static void LogMigrationNotice(
        BootstrapConfig config,
        V1EndpointSnapshot? v1,
        string configPath,
        PhaseLogger logger)
    {
        foreach (var line in FormatMigrationNotice(config, v1, configPath))
        {
            logger.Info(line);
        }
    }

    internal static IReadOnlyList<string> FormatMigrationNotice(
        BootstrapConfig config,
        V1EndpointSnapshot? v1,
        string configPath)
    {
        var lines = new List<string>();
        var backupPath = configPath + ".bak.v1";
        lines.Add($"    config: migrated schema v1 → v2 (backup: {backupPath})");

        if (!TryResolvePrimaryHost(config, out var primary) || primary is null)
        {
            return lines;
        }

        if (!TryFormatV2ApiUrl(config, primary, out var apiUrl))
        {
            return lines;
        }

        lines.Add($"      API:  {apiUrl}");

        if (config.Deployment.IncludeWeb
            && TryFormatV2WebUrl(config, primary, out var webUrl))
        {
            lines.Add($"      Web:  {webUrl}");
        }

        if (v1 is not null
            && v1.WebEnabled
            && v1.WebHttps != v1.ApiHttps
            && TryResolvePrimaryHost(v1.Hosts, out var v1PrimaryEntry)
            && v1PrimaryEntry is not null
            && TryFormatV1ApiUrl(HostParser.ToUrlHost(v1PrimaryEntry), v1.ApiHttps, out var oldApiUrl)
            && TryFormatV1WebUrl(HostParser.ToUrlHost(v1PrimaryEntry), v1.WebHttps, out var oldWebUrl))
        {
            lines.Add($"      (was: API {oldApiUrl}, web {oldWebUrl})");
        }

        return lines;
    }

    private static bool TryResolvePrimaryHost(BootstrapConfig config, out HostEntry? primary)
    {
        return TryResolvePrimaryHost(config.Edge.Hosts, out primary);
    }

    private static bool TryResolvePrimaryHost(IReadOnlyList<string> hosts, out HostEntry? primary)
    {
        primary = null;
        if (hosts.Count == 0)
        {
            return false;
        }

        var parsed = new List<HostEntry>(hosts.Count);
        foreach (var raw in hosts)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                parsed.Add(HostParser.Parse(raw));
            }
            catch (FormatException)
            {
            }
        }

        primary = HostParser.PickPrimary(parsed);
        return primary is not null;
    }

    private static bool TryFormatV2ApiUrl(BootstrapConfig config, HostEntry primary, out string url)
    {
        url = string.Empty;
        if (config.Edge.Routing.Mode == EdgeRoutingMode.Subdomain
            && !string.IsNullOrWhiteSpace(config.Edge.Routing.ApiHost))
        {
            return TryFormatHttpsUrl(config.Edge.Routing.ApiHost.Trim(), config.Edge.Ports.Https, "/api/", out url);
        }

        return TryFormatHttpsUrl(HostParser.ToUrlHost(primary), config.Edge.Ports.Https, "/api/", out url);
    }

    private static bool TryFormatV2WebUrl(BootstrapConfig config, HostEntry primary, out string url)
    {
        url = string.Empty;
        if (config.Edge.Routing.Mode == EdgeRoutingMode.Subdomain
            && !string.IsNullOrWhiteSpace(config.Edge.Routing.WebHost))
        {
            return TryFormatHttpsUrl(config.Edge.Routing.WebHost.Trim(), config.Edge.Ports.Https, "/", out url);
        }

        return TryFormatHttpsUrl(HostParser.ToUrlHost(primary), config.Edge.Ports.Https, "/", out url);
    }

    private static bool TryFormatV1ApiUrl(string host, int apiHttps, out string url)
        => TryFormatHttpsUrl(host, apiHttps, "/", out url);

    private static bool TryFormatV1WebUrl(string host, int webHttps, out string url)
        => TryFormatHttpsUrl(host, webHttps, "/", out url);

    private static bool TryFormatHttpsUrl(string host, int port, string pathSuffix, out string url)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            url = string.Empty;
            return false;
        }

        var portSuffix = port == DefaultHttpsPort ? string.Empty : $":{port}";
        url = $"https://{host}{portSuffix}{pathSuffix}";
        return true;
    }
}
