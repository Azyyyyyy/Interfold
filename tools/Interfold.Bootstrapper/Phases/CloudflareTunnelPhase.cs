using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Shared.Contracts.Configuration;
using Spectre.Console;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Post-launch hand-hold: wait for the tunnel connector, PUT ingress, upsert DNS CNAMEs,
/// then verify public HTTPS readiness.
/// </summary>
internal static class CloudflareTunnelPhase
{
    private static readonly TimeSpan ConnectorTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PublicHealthTimeout = TimeSpan.FromMinutes(5);
    internal const string OriginService = "http://edge-nginx:80";

    public static async Task RunAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        PhaseLogger logger,
        CancellationToken ct)
    {
        const string Phase = "cloudflare-tunnel";
        if (!config.Edge.Cloudflare.Enabled || options.SkipCloudflareTunnel)
        {
            return;
        }

        logger.PhaseStart(Phase);

        var state = LoadState(options.OutputDir)
            ?? throw new InvalidOperationException(
                "Cloudflare tunnel state missing; re-run publish so the connector token is provisioned.");

        using var client = CloudflareTunnelClient.Create(config.Edge.Cloudflare.ApiToken);
        await client.WaitUntilHealthyAsync(state.AccountId, state.TunnelId, ConnectorTimeout, logger, ct)
            .ConfigureAwait(false);

        var hostnames = ResolvePublicHostnames(config);
        if (hostnames.Count == 0)
        {
            throw new InvalidOperationException("No DNS hostnames available for Cloudflare Tunnel ingress.");
        }

        if (!options.NonInteractive)
        {
            var table = new Table().AddColumn("Hostname").AddColumn("Origin").AddColumn("DNS");
            foreach (var host in hostnames)
            {
                table.AddRow(host, OriginService, $"CNAME → {state.TunnelId}.cfargotunnel.com (proxied)");
            }

            AnsiConsole.Write(table);
            if (!AnsiConsole.Confirm("Apply Cloudflare Tunnel ingress and DNS records?", defaultValue: true))
            {
                logger.Warn("operator declined Cloudflare Tunnel hostname registration");
                logger.PhaseDone(Phase);
                return;
            }
        }

        logger.Info($"    putting ingress for {hostnames.Count} hostname(s)");
        await client.PutIngressAsync(state.AccountId, state.TunnelId, hostnames, OriginService, ct)
            .ConfigureAwait(false);

        foreach (var host in hostnames)
        {
            var zone = await client.ResolveZoneAndAccountAsync(host, ct).ConfigureAwait(false);
            logger.Info($"    upserting DNS CNAME {host} → {state.TunnelId}.cfargotunnel.com (zone {zone.ZoneName})");
            await client.UpsertDnsCnameAsync(zone.ZoneId, host, state.TunnelId, ct).ConfigureAwait(false);
        }

        var primary = hostnames[0];
        var readyUrl = CloudflareTunnelClient.BuildPublicReadyUrl(primary);
        logger.Info($"    polling public {readyUrl} (up to {PublicHealthTimeout.TotalMinutes:F0}m)");
        await WaitForPublicReadyAsync(readyUrl, logger, ct).ConfigureAwait(false);

        logger.PhaseDone(Phase);
    }

    internal static IReadOnlyList<string> ResolvePublicHostnames(BootstrapConfig config)
    {
        var hosts = new List<string>();
        if (config.Edge.Routing.Mode == EdgeRoutingMode.Subdomain)
        {
            AddDns(hosts, config.Edge.Routing.ApiHost);
            if (config.Deployment.IncludeWeb)
                AddDns(hosts, config.Edge.Routing.WebHost);
            return hosts;
        }

        foreach (var raw in config.Edge.Hosts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var entry = HostParser.Parse(raw);
                if (entry.Kind == HostKind.Dns && entry.DnsName is not null)
                    AddDns(hosts, entry.DnsName);
            }
            catch (FormatException)
            {
            }
        }

        return hosts;
    }

    internal static string ConnectorTokenPath(string outputDir)
        => Path.Combine(outputDir, "secrets", "cloudflare-tunnel.token");

    internal static string StatePath(string outputDir)
        => Path.Combine(outputDir, ".cloudflare-tunnel.json");

    /// <summary>Relative to Aspire publish anchor (<c>{outputDir}/_aspire_anchor/inner</c>).</summary>
    internal static string ConnectorTokenAspireRelativePath => "../../secrets/cloudflare-tunnel.token";

    internal static async Task EnsureTunnelArtifactsAsync(
        BootstrapConfig config,
        string outputDir,
        PhaseLogger logger,
        CancellationToken ct)
    {
        if (!config.Edge.Cloudflare.Enabled)
            return;

        var hostnames = ResolvePublicHostnames(config);
        if (hostnames.Count == 0)
        {
            throw new InvalidOperationException(
                "Cloudflare Tunnel requires at least one DNS hostname in edge.hosts or routing hosts.");
        }

        using var client = CloudflareTunnelClient.Create(config.Edge.Cloudflare.ApiToken);
        var zone = await client.ResolveZoneAndAccountAsync(hostnames[0], ct).ConfigureAwait(false);
        logger.Info($"    cloudflare zone={zone.ZoneName} account={zone.AccountId}");

        var tunnelName = config.Edge.Cloudflare.TunnelName.Trim();
        var creds = await client.EnsureTunnelAsync(zone.AccountId, tunnelName, ct).ConfigureAwait(false);
        logger.Info($"    cloudflare tunnel id={creds.TunnelId} name={creds.Name}");

        var tokenPath = ConnectorTokenPath(outputDir);
        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
        await File.WriteAllTextAsync(tokenPath, creds.ConnectorToken.Trim(), ct).ConfigureAwait(false);
        UnixFilePermissions.SetOwnerOnly(tokenPath, logger, "cloudflare tunnel token");

        var state = new CloudflareTunnelState
        {
            TunnelId = creds.TunnelId,
            AccountId = zone.AccountId,
            ZoneId = zone.ZoneId,
            Name = creds.Name,
        };
        var stateJson = JsonSerializer.Serialize(state, BootstrapJsonContext.Default.CloudflareTunnelState);
        await File.WriteAllTextAsync(StatePath(outputDir), stateJson, ct).ConfigureAwait(false);
    }

    private static CloudflareTunnelState? LoadState(string outputDir)
    {
        var path = StatePath(outputDir);
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.CloudflareTunnelState);
    }

    private static void AddDns(List<string> hosts, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;
        var host = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (!hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            hosts.Add(host);
    }

    private static async Task WaitForPublicReadyAsync(string readyUrl, PhaseLogger logger, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.UtcNow + PublicHealthTimeout;
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                var resp = await http.GetAsync(readyUrl, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    logger.Info($"    public api ready at {readyUrl} after {attempt} attempt(s)");
                    return;
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Public health check did not return 200 at {readyUrl} within {PublicHealthTimeout.TotalMinutes:F0}m. " +
            "Verify ingress hostnames, proxied DNS CNAMEs, and cloudflared/edge-nginx logs.");
    }
}
