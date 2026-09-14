using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Interfold.Bootstrapper.Cli;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.Util;

/// <summary>Cloudflare REST client for remotely-managed tunnels (API token only — never the connector token).</summary>
internal sealed class CloudflareTunnelClient : IDisposable
{
    private readonly HttpClient _http;

    internal CloudflareTunnelClient(HttpClient http)
    {
        _http = http;
    }

    public void Dispose() => _http.Dispose();

    internal const string DefaultApiBaseUrl = "https://api.cloudflare.com/client/v4/";

    /// <summary>Optional override for DinD / unit tests (<c>INTERFOLD_CLOUDFLARE_API_BASE_URL</c>).</summary>
    internal static string ResolveApiBaseUrl()
    {
        var overrideBase = Environment.GetEnvironmentVariable("INTERFOLD_CLOUDFLARE_API_BASE_URL");
        if (string.IsNullOrWhiteSpace(overrideBase))
            return DefaultApiBaseUrl;

        var trimmed = overrideBase.Trim();
        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }

    internal static CloudflareTunnelClient Create(string apiToken)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            BaseAddress = new Uri(ResolveApiBaseUrl()),
            Timeout = TimeSpan.FromMinutes(2),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
        http.DefaultRequestHeaders.UserAgent.ParseAdd(BootstrapperVersion.UserAgent);
        return new CloudflareTunnelClient(http);
    }

    internal async Task<CloudflareZoneAccount> ResolveZoneAndAccountAsync(string hostname, CancellationToken ct)
    {
        foreach (var candidate in EnumerateZoneCandidates(hostname))
        {
            using var response = await _http
                .GetAsync($"zones?name={Uri.EscapeDataString(candidate)}", ct)
                .ConfigureAwait(false);
            var zones = await ReadResultAsync(
                    response,
                    CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareZoneResult,
                    ct)
                .ConfigureAwait(false);
            if (zones is null || zones.Count == 0)
                continue;

            var zone = zones[0];
            var zoneId = zone.Id;
            var accountId = zone.Account?.Id;
            if (string.IsNullOrWhiteSpace(zoneId) || string.IsNullOrWhiteSpace(accountId))
            {
                throw new InvalidOperationException(
                    $"Cloudflare zone '{candidate}' response missing id/account.id.");
            }

            return new CloudflareZoneAccount(zoneId, accountId, zone.Name ?? candidate);
        }

        throw new InvalidOperationException(
            $"No Cloudflare zone found for hostname '{hostname}'. " +
            "Confirm the domain is on Cloudflare and the API token has Zone:DNS:Edit.");
    }

    internal async Task<CloudflareTunnelCredentials> EnsureTunnelAsync(
        string accountId,
        string tunnelName,
        CancellationToken ct)
    {
        var existing = await FindTunnelByNameAsync(accountId, tunnelName, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var connectorToken = await GetConnectorTokenAsync(accountId, existing.Value.Id, ct).ConfigureAwait(false);
            return new CloudflareTunnelCredentials(existing.Value.Id, tunnelName, connectorToken);
        }

        using var content = JsonBody(
            new CloudflareCreateTunnelRequest { Name = tunnelName },
            CloudflareApiJsonContext.Default.CloudflareCreateTunnelRequest);
        using var response = await _http
            .PostAsync($"accounts/{accountId}/cfd_tunnel", content, ct)
            .ConfigureAwait(false);
        var created = await ReadRequiredResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareTunnelResult,
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(created.Id) || string.IsNullOrWhiteSpace(created.Token))
            throw new InvalidOperationException("Cloudflare tunnel create response missing id/token.");

        return new CloudflareTunnelCredentials(created.Id, tunnelName, created.Token);
    }

    internal async Task<string> GetConnectorTokenAsync(string accountId, string tunnelId, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/cfd_tunnel/{tunnelId}/token", ct)
            .ConfigureAwait(false);
        // Token endpoint returns { success, result: "<jwt>" } — result is a string.
        var token = await ReadRequiredResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseString,
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"Cloudflare tunnel token missing for {tunnelId}.");

        return token;
    }

    internal async Task WaitUntilHealthyAsync(
        string accountId,
        string tunnelId,
        TimeSpan timeout,
        PhaseLogger logger,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var status = await GetTunnelStatusAsync(accountId, tunnelId, ct).ConfigureAwait(false);
            if (status.IsHealthy)
            {
                logger.Info($"    cloudflare tunnel {tunnelId} is healthy ({status.ConnectionCount} connection(s))");
                return;
            }

            logger.Info($"    waiting for tunnel connector (status={status.Status}, connections={status.ConnectionCount})");
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Cloudflare tunnel {tunnelId} did not become healthy within {timeout.TotalMinutes:F0}m. " +
            "Check cloudflared logs, egress to Cloudflare on port 7844, and the connector token file.");
    }

    internal async Task PutIngressAsync(
        string accountId,
        string tunnelId,
        IReadOnlyList<string> hostnames,
        string originService,
        CancellationToken ct)
    {
        var ingress = new List<CloudflareIngressRule>(hostnames.Count + 1);
        foreach (var hostname in hostnames)
        {
            ingress.Add(new CloudflareIngressRule
            {
                Hostname = hostname,
                Service = originService,
                OriginRequest = new CloudflareOriginRequest(),
            });
        }

        ingress.Add(new CloudflareIngressRule { Service = "http_status:404" });

        using var content = JsonBody(
            new CloudflarePutIngressRequest { Config = new CloudflareTunnelConfig { Ingress = ingress } },
            CloudflareApiJsonContext.Default.CloudflarePutIngressRequest);
        using var response = await _http
            .PutAsync($"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations", content, ct)
            .ConfigureAwait(false);
        _ = await ReadEnvelopeAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseJsonElement,
                ct)
            .ConfigureAwait(false);
    }

    internal async Task UpsertDnsCnameAsync(
        string zoneId,
        string hostname,
        string tunnelId,
        CancellationToken ct)
    {
        var request = new CloudflareDnsRecordRequest
        {
            Name = hostname,
            Content = $"{tunnelId}.cfargotunnel.com",
            Proxied = true,
            Ttl = 1,
        };
        using var content = JsonBody(request, CloudflareApiJsonContext.Default.CloudflareDnsRecordRequest);
        var existingId = await FindDnsRecordIdAsync(zoneId, hostname, ct).ConfigureAwait(false);

        if (existingId is null)
        {
            using var response = await _http
                .PostAsync($"zones/{zoneId}/dns_records", content, ct)
                .ConfigureAwait(false);
            _ = await ReadEnvelopeAsync(
                    response,
                    CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareDnsRecordResult,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        using var patchResponse = await _http
            .PatchAsync($"zones/{zoneId}/dns_records/{existingId}", content, ct)
            .ConfigureAwait(false);
        _ = await ReadEnvelopeAsync(
                patchResponse,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareDnsRecordResult,
                ct)
            .ConfigureAwait(false);
    }

    internal static IEnumerable<string> EnumerateZoneCandidates(string hostname)
    {
        var host = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(host))
            yield break;

        yield return host;
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < parts.Length - 1; i++)
            yield return string.Join('.', parts.AsSpan(i));

        if (parts.Length >= 2)
            yield return string.Join('.', parts[^2], parts[^1]);
    }

    internal static string BuildPublicReadyUrl(string hostname)
        => $"https://{hostname.Trim().TrimEnd('.')}{HealthEndpoints.Ready}";

    private async Task<(string Id, string Name)?> FindTunnelByNameAsync(
        string accountId,
        string name,
        CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(
                $"accounts/{accountId}/cfd_tunnel?name={Uri.EscapeDataString(name)}&is_deleted=false",
                ct)
            .ConfigureAwait(false);
        var tunnels = await ReadResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareTunnelResult,
                ct)
            .ConfigureAwait(false);
        if (tunnels is null)
            return null;

        foreach (var tunnel in tunnels)
        {
            if (string.IsNullOrWhiteSpace(tunnel.Id))
                continue;
            if (string.Equals(tunnel.Name, name, StringComparison.OrdinalIgnoreCase))
                return (tunnel.Id, tunnel.Name ?? name);
        }

        return null;
    }

    private async Task<TunnelStatus> GetTunnelStatusAsync(string accountId, string tunnelId, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/cfd_tunnel/{tunnelId}", ct)
            .ConfigureAwait(false);
        var result = await ReadRequiredResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareTunnelResult,
                ct)
            .ConfigureAwait(false);
        var status = string.IsNullOrWhiteSpace(result.Status) ? "unknown" : result.Status;
        var connections = result.Connections.Count;
        var healthy = connections > 0
            || string.Equals(status, "healthy", StringComparison.OrdinalIgnoreCase);
        return new TunnelStatus(status, connections, healthy);
    }

    private async Task<string?> FindDnsRecordIdAsync(string zoneId, string hostname, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(
                $"zones/{zoneId}/dns_records?type=CNAME&name={Uri.EscapeDataString(hostname)}",
                ct)
            .ConfigureAwait(false);
        var records = await ReadResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareDnsRecordResult,
                ct)
            .ConfigureAwait(false);
        if (records is null || records.Count == 0)
            return null;

        return records[0].Id;
    }

    private static StringContent JsonBody<T>(T value, JsonTypeInfo<T> typeInfo)
        => new(JsonSerializer.Serialize(value, typeInfo), Encoding.UTF8, "application/json");

    private static async Task<TResult> ReadRequiredResultAsync<TResult>(
        HttpResponseMessage response,
        JsonTypeInfo<CloudflareApiResponse<TResult>> typeInfo,
        CancellationToken ct)
    {
        var envelope = await ReadEnvelopeAsync(response, typeInfo, ct).ConfigureAwait(false);
        if (envelope.Result is null)
            throw new InvalidOperationException("Cloudflare API response missing result.");
        return envelope.Result;
    }

    private static async Task<TResult?> ReadResultAsync<TResult>(
        HttpResponseMessage response,
        JsonTypeInfo<CloudflareApiResponse<TResult>> typeInfo,
        CancellationToken ct)
    {
        var envelope = await ReadEnvelopeAsync(response, typeInfo, ct).ConfigureAwait(false);
        return envelope.Result;
    }

    private static async Task<CloudflareApiResponse<TResult>> ReadEnvelopeAsync<TResult>(
        HttpResponseMessage response,
        JsonTypeInfo<CloudflareApiResponse<TResult>> typeInfo,
        CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        CloudflareApiResponse<TResult>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Cloudflare API returned non-JSON (HTTP {(int)response.StatusCode}): {Truncate(json)}", ex);
        }

        if (envelope is null)
        {
            throw new InvalidOperationException(
                $"Cloudflare API returned empty JSON (HTTP {(int)response.StatusCode}).");
        }

        if (!response.IsSuccessStatusCode || !envelope.Success)
        {
            var errors = FormatErrors(envelope.Errors);
            throw new InvalidOperationException(
                $"Cloudflare API error: {(string.IsNullOrEmpty(errors) ? $"HTTP {(int)response.StatusCode}" : errors)}");
        }

        return envelope;
    }

    private static string FormatErrors(List<CloudflareApiError> errors)
    {
        if (errors.Count == 0)
            return string.Empty;

        return string.Join("; ", errors.Select(err =>
        {
            var code = err.Code is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } el
                ? el.ToString()
                : "?";
            return $"{code}: {err.Message}";
        }));
    }

    private static string Truncate(string s)
        => s.Length <= 200 ? s : s[..200] + "…";

    private readonly record struct TunnelStatus(string Status, int ConnectionCount, bool IsHealthy);
}

internal sealed record CloudflareZoneAccount(string ZoneId, string AccountId, string ZoneName);

internal sealed record CloudflareTunnelCredentials(string TunnelId, string Name, string ConnectorToken);

public sealed class CloudflareTunnelState
{
    [JsonPropertyName("tunnelId")]
    public string TunnelId { get; set; } = string.Empty;

    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("zoneId")]
    public string ZoneId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
