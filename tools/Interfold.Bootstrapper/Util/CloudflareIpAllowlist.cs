using System.Net.Http;
using System.Text;
using Interfold.Bootstrapper.Cli;

namespace Interfold.Bootstrapper.Util;

/// <summary>Builds the nginx Cloudflare <c>real_ip</c> + <c>allow</c> snippet from
/// Cloudflare's published IPv4/IPv6 range lists (refreshed at publish time).</summary>
internal static class CloudflareIpAllowlist
{
    internal const string Ipv4Url = "https://www.cloudflare.com/ips-v4";
    internal const string Ipv6Url = "https://www.cloudflare.com/ips-v6";

    /// <summary>Writes an empty/no-op snippet (no IP filtering).</summary>
    internal static void WriteDisabled(string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath,
            "# Cloudflare IP allowlist disabled (deployment.edge.cloudflare.ipAllowlist=false).\n");
    }

    /// <summary>Fetches current Cloudflare ranges and writes nginx config. Falls back to
    /// the embedded snapshot when the network fetch fails.</summary>
    internal static async Task WriteAllowlistAsync(
        string targetPath,
        PhaseLogger logger,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        string? v4 = null;
        string? v6 = null;
        var owned = httpClient is null;
        httpClient ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            v4 = await httpClient.GetStringAsync(new Uri(Ipv4Url), ct).ConfigureAwait(false);
            v6 = await httpClient.GetStringAsync(new Uri(Ipv6Url), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.Warn(
                $"    Cloudflare IP fetch failed ({ex.GetType().Name}: {ex.Message}); " +
                "using embedded snapshot");
            File.WriteAllText(targetPath, EmbeddedSupportFiles.ReadAllText(
                EmbeddedSupportFiles.EdgeCloudflareIpsRelative));
            return;
        }
        finally
        {
            if (owned) httpClient.Dispose();
        }

        File.WriteAllText(targetPath, BuildSnippet(v4!, v6!));
        logger.Info($"    wrote Cloudflare IP allowlist → {targetPath}");
    }

    internal static string BuildSnippet(string ipv4Body, string ipv6Body)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated from https://www.cloudflare.com/ips-v4 and ips-v6");
        sb.AppendLine("# real_ip restores the visitor address behind orange-cloud.");
        sb.AppendLine("real_ip_header CF-Connecting-IP;");
        sb.AppendLine("real_ip_recursive on;");

        foreach (var cidr in ParseCidrs(ipv4Body).Concat(ParseCidrs(ipv6Body)))
        {
            sb.Append("set_real_ip_from ").Append(cidr).AppendLine(";");
        }

        foreach (var cidr in ParseCidrs(ipv4Body).Concat(ParseCidrs(ipv6Body)))
        {
            sb.Append("allow ").Append(cidr).AppendLine(";");
        }

        sb.AppendLine("deny all;");
        return sb.ToString();
    }

    private static IEnumerable<string> ParseCidrs(string body)
    {
        using var reader = new StringReader(body);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            yield return trimmed;
        }
    }
}
